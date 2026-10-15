using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Gateway.TestHarness;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// The contract files, read as text, so an assertion in this suite compares the gateway against the
/// document rather than against a copy of it. CODING_STANDARDS.md -> "A guard reads the thing it
/// guards": a cookie name, an error code or a header list repeated here would still agree with
/// itself after the contract changed.
///
/// <para>Rule ids are the exception, and deliberately so: they come from
/// <see cref="Platform.Abstractions.RuleIds"/>, which ADR-0015 put in the abstractions precisely so
/// four workstreams cannot keep four copies, and a test in <c>tests/Platform.Abstractions.Tests</c>
/// already holds that type to this contract's table. Reading the table twice would add nothing.</para>
///
/// <para><c>Gateway.IntegrationTests</c> has a reader of its own for the two contracts its rows need.
/// The suites do not share one: this project references the harness and nothing else, which is what
/// keeps the negative suite from depending on the functional suite's internals.</para>
///
/// <para>Each parse fails loudly if the shape it reads is no longer there. A silent empty list would
/// make every assertion built on it vacuous, which is the failure mode CODING_STANDARDS.md -> "An
/// assertion must be able to fail" names.</para>
/// </summary>
internal static partial class Contract
{
    /// <summary>Where <c>form-action</c> takes the issuer's browser origin in the baseline policy.</summary>
    public const string IssuerOriginPlaceholder = "<identity provider origin>";

    /// <summary>No header under this prefix is ever sent (contracts/http-contract.md -> Response headers).</summary>
    public const string ForbiddenHeaderPrefix = "Access-Control-";

    /// <summary>
    /// The platform cookie names, from the <b>rows</b> of the Cookies table in
    /// <c>contracts/http-contract.md</c>. The section's prose also backquotes the bare
    /// <c>__Host-</c> prefix, which is not a cookie, so only table rows are read. A wildcard name
    /// (<c>__Host-spa-corr.*</c>) keeps its star: a test substitutes a suffix for it, as the
    /// framework does per login.
    /// </summary>
    public static ImmutableArray<string> PlatformCookieNames { get; } = ReadPlatformCookieNames();

    /// <summary>
    /// The prefix the gate's cookie-name rule is written against, from
    /// <c>contracts/request-gate.md</c> -> "The rule that covers every path". Read from there rather
    /// than typed, because it is the subject of the rule N-25 exercises.
    /// </summary>
    public static string PlatformCookiePrefix { get; } = ReadPlatformCookiePrefix();

    /// <summary>
    /// The response headers with a fixed value, from the bullet list under "Response headers". The
    /// <c>Content-Security-Policy</c> bullet names no value and is left out, and so is the prose
    /// bullet about <c>Access-Control-*</c>, which <see cref="ForbiddenHeaderPrefix"/> covers.
    /// </summary>
    public static ImmutableDictionary<string, string> FixedResponseHeaders { get; } = ReadFixedResponseHeaders();

    /// <summary>
    /// The baseline content security policy as one line, with <see cref="IssuerOriginPlaceholder"/>
    /// still in it. The fenced block is wrapped over three lines for the page's width, which is not
    /// part of the header's value, so the lines are joined with a single space.
    /// </summary>
    public static string BaselineCspTemplate { get; } = ReadBaselineCsp();

    /// <summary>The status and code pairs of the Errors table, with the "When" column that names each.</summary>
    public static ImmutableArray<ErrorRow> Errors { get; } = ReadErrors();

    /// <summary>The baseline with the issuer browser origin the gateway was configured with spliced in.</summary>
    public static string BaselineCsp(string issuerBrowserOrigin) =>
        BaselineCspTemplate.Replace(IssuerOriginPlaceholder, issuerBrowserOrigin, StringComparison.Ordinal);

    /// <summary>One policy as its directives, keyed by directive name, in the order they were sent.</summary>
    public static ImmutableArray<KeyValuePair<string, string>> Directives(string policy) =>
        [.. policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directive => directive.Split(' ', 2))
            .Select(parts => new KeyValuePair<string, string>(parts[0], parts.Length > 1 ? parts[1] : string.Empty))];

    /// <summary>
    /// The one Errors row whose "When" column contains <paramref name="whenContains"/>, so a test
    /// names the case the contract names and takes the status and the code from the table. It throws
    /// when no row or more than one matches, because either would leave the assertion reading
    /// something other than the case it meant.
    /// </summary>
    public static ErrorRow Error(string whenContains)
    {
        ErrorRow[] matched = [.. Errors.Where(row => row.When.Contains(whenContains, StringComparison.OrdinalIgnoreCase))];
        return matched.Length == 1
            ? matched[0]
            : throw new InvalidOperationException(
                $"contracts/http-contract.md -> Errors has {matched.Length} rows whose 'When' contains "
                + $"'{whenContains}'. This suite reads the status and code from that table.");
    }

    /// <summary>One row of the Errors table.</summary>
    public sealed record ErrorRow(int Status, string Code, string When);

    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "contracts", name));

    private static string Section(string contract, string from, string to)
    {
        int start = contract.IndexOf(from, StringComparison.Ordinal);
        int end = contract.IndexOf(to, StringComparison.Ordinal);
        return start >= 0 && end > start
            ? contract[start..end]
            : throw new InvalidOperationException(
                $"contracts/http-contract.md no longer has '{from}' before '{to}'. This suite reads it from there.");
    }

    private static ImmutableArray<string> ReadPlatformCookieNames()
    {
        string cookies = Section(Read("http-contract.md"), "## Cookies", "## Request headers the SPA sends");
        ImmutableArray<string> names =
            [.. cookies.Split('\n')
                .Where(line => line.StartsWith("| `", StringComparison.Ordinal))
                .SelectMany(line => BackquotedName().Matches(line[..(line.IndexOf('|', 1) is int next && next > 0 ? next : line.Length)]))
                .Select(match => match.Groups["name"].Value)
                .Distinct(StringComparer.Ordinal)];

        return names.Length > 0
            ? names
            : throw new InvalidOperationException("contracts/http-contract.md -> Cookies names no cookie in its table rows.");
    }

    private static string ReadPlatformCookiePrefix()
    {
        Match match = CookiePrefixRule().Match(Read("request-gate.md"));
        return match.Success
            ? match.Groups["prefix"].Value
            : throw new InvalidOperationException(
                "contracts/request-gate.md no longer says 'A cookie name that begins `...`'. N-25 reads the prefix from there.");
    }

    private static ImmutableDictionary<string, string> ReadFixedResponseHeaders()
    {
        string headers = Section(Read("http-contract.md"), "## Response headers", "### The baseline content security policy");
        ImmutableDictionary<string, string> parsed = HeaderBullet()
            .Matches(headers)
            .ToImmutableDictionary(
                bullet => bullet.Groups["name"].Value,
                bullet => bullet.Groups["value"].Value,
                StringComparer.OrdinalIgnoreCase);

        return parsed.Count > 0
            ? parsed
            : throw new InvalidOperationException(
                "contracts/http-contract.md -> Response headers listed no header of the form `Name: value`.");
    }

    private static string ReadBaselineCsp()
    {
        Match block = BaselineBlock().Match(Read("http-contract.md"));
        if (!block.Success)
        {
            throw new InvalidOperationException(
                "contracts/http-contract.md no longer has a fenced baseline policy under "
                + "'The baseline content security policy'. This suite reads it from there.");
        }

        return string.Join(' ', block.Groups["policy"].Value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static ImmutableArray<ErrorRow> ReadErrors()
    {
        string errors = Read("http-contract.md");
        int start = errors.IndexOf("## Errors", StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("contracts/http-contract.md has no '## Errors' section.");
        }

        ImmutableArray<ErrorRow> rows =
            [.. ErrorTableRow().Matches(errors[start..])
                .Select(row => new ErrorRow(
                    int.Parse(row.Groups["status"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    row.Groups["code"].Value,
                    row.Groups["when"].Value.Trim()))];

        return rows.Length > 0
            ? rows
            : throw new InvalidOperationException("contracts/http-contract.md -> Errors listed no rows.");
    }

    [GeneratedRegex(@"### The baseline content security policy\s*```text\r?\n(?<policy>.*?)```", RegexOptions.Singleline)]
    private static partial Regex BaselineBlock();

    [GeneratedRegex(@"^- `(?<name>[A-Za-z][A-Za-z0-9-]*): (?<value>[^`]+)`", RegexOptions.Multiline)]
    private static partial Regex HeaderBullet();

    [GeneratedRegex(@"`(?<name>[^`]+)`")]
    private static partial Regex BackquotedName();

    [GeneratedRegex(@"^\| (?<status>\d{3}) \| `(?<code>[a-z_]+)` \| (?<when>[^|]+)\|", RegexOptions.Multiline)]
    private static partial Regex ErrorTableRow();

    [GeneratedRegex(@"A cookie name that begins `(?<prefix>[^`]+)`")]
    private static partial Regex CookiePrefixRule();
}
