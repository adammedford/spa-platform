using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Gateway.TestHarness;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// The contract files, read as text, so that an assertion in this folder compares the gateway against
/// the document rather than against a copy of it. CODING_STANDARDS.md -> "A guard reads the thing it
/// guards": a baseline policy or a field list repeated here would still agree with itself after the
/// contract changed.
///
/// Only the two contracts these rows are held to are parsed: <c>contracts/http-contract.md</c> for the
/// response headers, the baseline content security policy and the Errors table, and
/// <c>contracts/telemetry.md</c> for the request line's fields. Each parse fails loudly if the shape it
/// reads is no longer there, because a silent empty list would make every assertion built on it vacuous.
/// </summary>
internal static partial class ContractText
{
    /// <summary>Where <c>form-action</c> takes the issuer's browser origin in the contract's baseline.</summary>
    public const string IssuerOriginPlaceholder = "<identity provider origin>";

    /// <summary>
    /// The baseline content security policy, from the fenced block under "The baseline content security
    /// policy" in <c>contracts/http-contract.md</c>, as one line. The block is wrapped over three lines
    /// for the page's width, which is not part of the header's value, so the lines are joined with a
    /// single space. <see cref="IssuerOriginPlaceholder"/> is still in it: the caller substitutes the
    /// origin the gateway was configured with.
    /// </summary>
    public static string BaselineCspTemplate { get; } = ReadBaselineCsp();

    /// <summary>
    /// The response headers with a fixed value, from the bullet list under "Response headers". The
    /// <c>Content-Security-Policy</c> bullet names no value and is left out, and so is the prose bullet
    /// about <c>Access-Control-*</c>, which <see cref="ForbiddenHeaderPrefix"/> covers instead.
    /// </summary>
    public static ImmutableDictionary<string, string> FixedResponseHeaders { get; } = ReadFixedResponseHeaders();

    /// <summary>No header under this prefix is ever sent (contracts/http-contract.md -> Response headers).</summary>
    public const string ForbiddenHeaderPrefix = "Access-Control-";

    /// <summary>
    /// The request line's field names, in the order the "Log fields" row of
    /// <c>contracts/telemetry.md</c> lists them.
    /// </summary>
    public static ImmutableArray<string> LogFields { get; } = ReadLogFields();

    /// <summary>The <c>route</c> values the same table allows.</summary>
    public static ImmutableArray<string> RouteValues { get; } = ReadTableNames("`route` values");

    /// <summary>The metric names the same table allows.</summary>
    public static ImmutableArray<string> MetricNames { get; } = ReadTableNames("Metrics");

    /// <summary>
    /// The projects under <c>src/</c>, which is what <c>Platform.&lt;Project&gt;</c> in
    /// <c>contracts/telemetry.md</c> -> The instruments can name. Read from the directory rather than
    /// listed, so a project added or renamed does not leave this agreeing with itself.
    /// </summary>
    public static ImmutableHashSet<string> PlatformInstrumentNames { get; } =
        [.. Directory.EnumerateDirectories(Path.Combine(FixtureFiles.RepositoryRoot, "src"))
            .Select(directory => Path.GetFileName(directory))
            .Where(name => name.StartsWith("Platform.", StringComparison.Ordinal))];

    /// <summary>The rows of the Errors table in <c>contracts/http-contract.md</c>.</summary>
    public static ImmutableArray<ErrorRow> Errors { get; } = ReadErrors();

    /// <summary>The baseline with the issuer origin the gateway was configured with spliced in.</summary>
    public static string BaselineCsp(string issuerBrowserOrigin) =>
        BaselineCspTemplate.Replace(IssuerOriginPlaceholder, issuerBrowserOrigin, StringComparison.Ordinal);

    /// <summary>
    /// The one Errors row whose "When" column contains <paramref name="whenContains"/>, so a test names the
    /// case the contract names and takes the status and the code from the table. It throws when no row or
    /// more than one matches, because either would leave the assertion reading another case than the one
    /// it meant.
    /// </summary>
    public static ErrorRow Error(string whenContains)
    {
        ErrorRow[] matched = [.. Errors.Where(row => row.When.Contains(whenContains, StringComparison.OrdinalIgnoreCase))];
        return matched.Length == 1
            ? matched[0]
            : throw new InvalidOperationException(
                $"contracts/http-contract.md -> Errors has {matched.Length} rows whose 'When' contains "
                + $"'{whenContains}'. The tests in this folder read the status and the code from that table.");
    }

    /// <summary>One row of the Errors table: the status, the problem body's <c>code</c>, and the case its "When" column names.</summary>
    public sealed record ErrorRow(int Status, string Code, string When);

    /// <summary>One policy as its directives, keyed by directive name, in the order they were sent.</summary>
    public static ImmutableArray<KeyValuePair<string, string>> Directives(string policy) =>
        [.. policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directive => directive.Split(' ', 2))
            .Select(parts => new KeyValuePair<string, string>(parts[0], parts.Length > 1 ? parts[1] : string.Empty))];

    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "contracts", name));

    private static string ReadBaselineCsp()
    {
        string contract = Read("http-contract.md");
        Match block = BaselineBlock().Match(contract);
        if (!block.Success)
        {
            throw new InvalidOperationException(
                "contracts/http-contract.md no longer has a fenced baseline policy under "
                + "'The baseline content security policy'. The tests in this folder read it from there.");
        }

        return string.Join(' ', block.Groups["policy"].Value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static ImmutableDictionary<string, string> ReadFixedResponseHeaders()
    {
        string contract = Read("http-contract.md");
        int start = contract.IndexOf("## Response headers", StringComparison.Ordinal);
        int end = contract.IndexOf("### The baseline content security policy", StringComparison.Ordinal);
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException(
                "contracts/http-contract.md no longer has a 'Response headers' section before the baseline policy.");
        }

        ImmutableDictionary<string, string> headers = HeaderBullet()
            .Matches(contract[start..end])
            .ToImmutableDictionary(
                bullet => bullet.Groups["name"].Value,
                bullet => bullet.Groups["value"].Value,
                StringComparer.OrdinalIgnoreCase);

        return headers.Count > 0
            ? headers
            : throw new InvalidOperationException(
                "contracts/http-contract.md -> Response headers listed no header of the form `Name: value`.");
    }

    private static ImmutableArray<ErrorRow> ReadErrors()
    {
        string contract = Read("http-contract.md");
        int start = contract.IndexOf("## Errors", StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException(
                "contracts/http-contract.md has no '## Errors' section. The tests in this folder read it from there.");
        }

        ImmutableArray<ErrorRow> rows =
            [.. ErrorTableRow().Matches(contract[start..])
                .Select(row => new ErrorRow(
                    int.Parse(row.Groups["status"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    row.Groups["code"].Value,
                    row.Groups["when"].Value.Trim()))];

        return rows.Length > 0
            ? rows
            : throw new InvalidOperationException("contracts/http-contract.md -> Errors listed no rows.");
    }

    private static ImmutableArray<string> ReadLogFields() => ReadTableNames("Log fields");

    /// <summary>
    /// One row of the table in <c>contracts/telemetry.md</c>, as the backquoted names in its second
    /// column, in order.
    /// </summary>
    private static ImmutableArray<string> ReadTableNames(string kind)
    {
        string contract = Read("telemetry.md");
        string prefix = "| " + kind + " |";
        string? row = contract.Split('\n').FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"contracts/telemetry.md has no table row '{prefix}'. The tests in this folder read it from there.");

        ImmutableArray<string> names =
            [.. BackquotedName().Matches(row[prefix.Length..]).Select(name => name.Groups["name"].Value)];

        return names.Length > 0
            ? names
            : throw new InvalidOperationException($"contracts/telemetry.md's '{kind}' row names nothing.");
    }

    [GeneratedRegex(@"### The baseline content security policy\s*```text\r?\n(?<policy>.*?)```", RegexOptions.Singleline)]
    private static partial Regex BaselineBlock();

    [GeneratedRegex(@"^- `(?<name>[A-Za-z][A-Za-z0-9-]*): (?<value>[^`]+)`", RegexOptions.Multiline)]
    private static partial Regex HeaderBullet();

    [GeneratedRegex(@"`(?<name>[^`]+)`")]
    private static partial Regex BackquotedName();

    [GeneratedRegex(@"^\| (?<status>\d{3}) \| `(?<code>[a-z_]+)` \| (?<when>[^|]+)\|", RegexOptions.Multiline)]
    private static partial Regex ErrorTableRow();
}
