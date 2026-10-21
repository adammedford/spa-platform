using System.Collections.Immutable;
using System.Globalization;

namespace Gateway.TestHarness;

/// <summary>
/// The text functions behind <see cref="Contract"/>: each takes a contract file's text and returns
/// the one table, list or block <see cref="Contract"/> exposes, or throws naming the file and the
/// section when that shape is not there. Internal, and open to <c>Gateway.IntegrationTests</c> only so
/// <c>Harness/ContractTests</c> can feed them planted text (#139: "the parsers take text ... each is
/// proven on planted input that breaks it before it is asserted against the live tree"). Seven are
/// proven that way: <see cref="ErrorRows"/> (a second table and a table without its separator among
/// the breaks, which prove <see cref="ContractSection.Rows"/> reads every table in a section),
/// <see cref="Error"/>, <see cref="CookieNames"/>, <see cref="BaselineCspTemplate"/>,
/// <see cref="FixedResponseHeaders"/> (an empty value), <see cref="ForbiddenHeaderPrefix"/> and
/// <see cref="TelemetryNames"/>. <see cref="BaselineCsp"/> is fed only unbroken text there, and
/// <see cref="CookiePrefix"/> and <see cref="CsrfHeaderName"/> are read only from the live contract.
/// Nothing here reads a file; <see cref="Contract"/> does, once per member.
///
/// <para>No function returns empty. A shape that is missing throws an
/// <see cref="InvalidOperationException"/>, because an empty list would make every assertion built on
/// it vacuous (CODING_STANDARDS.md -> "An assertion must be able to fail").</para>
/// </summary>
internal static class ContractParser
{
    /// <summary>The browser contract, which holds every table here but two.</summary>
    internal const string BrowserContract = "contracts/http-contract.md";

    /// <summary>The request gate's contract, which holds the cookie-name rule.</summary>
    internal const string RequestGate = "contracts/request-gate.md";

    /// <summary>The telemetry contract, whose one table names the log fields, route values and metrics.</summary>
    internal const string TelemetryContract = "contracts/telemetry.md";

    /// <summary>
    /// The platform cookie names, from the first cell of each row of Cookies, in table order. Rows
    /// only: the section's prose backquotes the bare <c>__Host-</c> prefix, which is not a cookie. A
    /// wildcard name (<c>__Host-spa-corr.*</c>) keeps its star, for a test to substitute a suffix as
    /// the framework does per login.
    /// </summary>
    internal static ImmutableArray<string> CookieNames(string browserContract)
    {
        ContractSection cookies = ContractSection.Of(BrowserContract, browserContract, "## Cookies");
        ImmutableArray<string> names =
            [.. cookies.Rows().SelectMany(cells => ContractSection.Backquoted(cells[0])).Distinct(StringComparer.Ordinal)];
        return names.Length > 0 ? names : throw cookies.Broken("no row names a cookie in its first cell");
    }

    /// <summary>
    /// The prefix the gate's cookie-name rule is written against, from "The rule that covers every
    /// path": the subject of the rule N-25 exercises.
    /// </summary>
    internal static string CookiePrefix(string requestGate)
    {
        const string Rule = "A cookie name that begins ";
        ContractSection rule = ContractSection.Of(RequestGate, requestGate, "## The rule that covers every path");
        int at = rule.Text.IndexOf(Rule, StringComparison.Ordinal);
        ImmutableArray<string> names = at >= 0 ? ContractSection.Backquoted(rule.Text[(at + Rule.Length)..]) : [];
        return names.Length > 0 && rule.Text[at + Rule.Length] == '`'
            ? names[0]
            : throw rule.Broken($"no sentence says '{Rule}`...`'");
    }

    /// <summary>
    /// The header an unsafe <c>/api</c> call carries its antiforgery token in: the first cell of the one
    /// row of "Request headers the SPA sends" (N-06). A second row would leave the reader guessing
    /// which header is the token's, so it throws.
    /// </summary>
    internal static string CsrfHeaderName(string browserContract)
    {
        ContractSection headers = ContractSection.Of(BrowserContract, browserContract, "## Request headers the SPA sends");
        IReadOnlyList<IReadOnlyList<string>> rows = headers.Rows();
        return rows.Count == 1
            ? headers.OneName(rows[0][0], "the header cell")
            : throw headers.Broken($"{rows.Count} header rows where the reader expects the one antiforgery header");
    }

    /// <summary>
    /// The response headers with a fixed value: the "Response headers" bullets whose backquotes hold
    /// <c>Name: value</c>, names compared case-insensitively. The policy bullet backquotes only its
    /// name, and the forbidding bullet is <see cref="ForbiddenHeaderPrefix"/>'s, so neither is here.
    /// A bullet whose backquotes hold a colon but not a header name and a value after it throws: an
    /// empty value read as fixed would have the suites assert the gateway sends the header empty.
    /// </summary>
    internal static ImmutableDictionary<string, string> FixedResponseHeaders(string browserContract)
    {
        ContractSection headers = ContractSection.Of(BrowserContract, browserContract, "## Response headers");
        ImmutableDictionary<string, string> fixedHeaders = headers.Bullets()
            .Where(bullet => bullet.StartsWith('`'))
            .Select(bullet => ContractSection.Backquoted(bullet).FirstOrDefault() ?? string.Empty)
            .Where(pair => pair.Contains(':', StringComparison.Ordinal))
            .Select(pair => pair.Split(": ", 2))
            .Select(pair => pair.Length == 2
                && pair[0].Length > 0
                && pair[0].All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                && pair[1].Trim().Length > 0
                    ? pair
                    : throw headers.Broken($"the bullet '{string.Join(": ", pair)}' is not of the form `Name: value` with a value"))
            .ToImmutableDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
        return fixedHeaders.Count > 0 ? fixedHeaders : throw headers.Broken("no bullet of the form `Name: value`");
    }

    /// <summary>
    /// The header prefix no response ever carries, from the "Response headers" bullet that begins
    /// <c>No `Prefix*`</c>: the backquoted name without its star.
    /// </summary>
    internal static string ForbiddenHeaderPrefix(string browserContract)
    {
        ContractSection headers = ContractSection.Of(BrowserContract, browserContract, "## Response headers");
        string[] prefixes = [.. headers.Bullets()
            .Where(bullet => bullet.StartsWith("No `", StringComparison.Ordinal))
            .Select(bullet => ContractSection.Backquoted(bullet).FirstOrDefault() ?? string.Empty)
            .Where(name => name.Length > 1 && name.EndsWith('*'))
            .Select(name => name[..^1])];
        return prefixes.Length == 1
            ? prefixes[0]
            : throw headers.Broken($"{prefixes.Length} bullets of the form 'No `Prefix*`' where the reader expects one");
    }

    /// <summary>
    /// The baseline content security policy as one line, its placeholder still in it. The fenced block
    /// is wrapped for the page's width, which is not part of the header's value, so its lines are
    /// joined with a single space.
    /// </summary>
    internal static string BaselineCspTemplate(string browserContract)
    {
        ContractSection baseline = ContractSection.Of(BrowserContract, browserContract, "### The baseline content security policy");
        return string.Join(' ', baseline.FencedBlock()
            .Select(line => line.Trim())
            .Where(line => line.Length > 0));
    }

    /// <summary>
    /// The template with <paramref name="issuerBrowserOrigin"/> in place of its one angle-bracketed
    /// placeholder (<c>&lt;identity provider origin&gt;</c> in <c>form-action</c>), which is read from the
    /// template rather than typed, so the reader holds nothing the contract does not say.
    /// </summary>
    internal static string BaselineCsp(string template, string issuerBrowserOrigin)
    {
        int open = template.IndexOf('<', StringComparison.Ordinal);
        int close = open >= 0 ? template.IndexOf('>', open) : -1;
        return open >= 0 && close > open && template.IndexOf('<', close) < 0
            ? string.Concat(template.AsSpan(0, open), issuerBrowserOrigin, template.AsSpan(close + 1))
            : throw new InvalidOperationException(
                $"{BrowserContract} -> The baseline content security policy: the block does not hold exactly one "
                + "<placeholder> for the provider origin. The contract reader reads it from there.");
    }

    /// <summary>
    /// The backquoted names in the second cell of the telemetry table's row whose first cell is
    /// <paramref name="kind"/>, in order: "Log fields", "`route` values" or "Metrics".
    /// </summary>
    internal static ImmutableArray<string> TelemetryNames(string telemetry, string kind)
    {
        ContractSection table = ContractSection.Of(TelemetryContract, telemetry, "# Telemetry");
        IReadOnlyList<string> row = table.Rows().FirstOrDefault(cells => string.Equals(cells[0], kind, StringComparison.Ordinal))
            ?? throw table.Broken($"no row '{kind}'");
        ImmutableArray<string> names = row.Count > 1 ? ContractSection.Backquoted(row[1]) : [];
        return names.Length > 0 ? names : throw table.Broken($"the row '{kind}' names nothing");
    }

    /// <summary>
    /// The rows of <c>contracts/http-contract.md</c> -> Errors, in table order: the status, the
    /// <c>code</c> cell without its backquotes, and the "When" text.
    /// </summary>
    internal static ImmutableArray<Contract.ErrorRow> ErrorRows(string browserContract)
    {
        ContractSection errors = ContractSection.Of(BrowserContract, browserContract, "## Errors");
        return [.. errors.Rows().Select(cells => new Contract.ErrorRow(
            cells.Count >= 3 && int.TryParse(cells[0], NumberStyles.None, CultureInfo.InvariantCulture, out int status)
                ? status
                : throw errors.Broken($"the row '{string.Join(" | ", cells)}' has no status"),
            errors.OneName(cells[1], "a row's code cell"),
            cells[2]))];
    }

    /// <summary>
    /// The one row whose <c>code</c> cell is <paramref name="code"/>, exactly. The contract calls the
    /// code stable and promises nothing of the "When" prose, so the code is the key (#139). None, or
    /// two rows with one code, throws: either would leave a test reading another row than it meant.
    /// </summary>
    internal static Contract.ErrorRow Error(ImmutableArray<Contract.ErrorRow> rows, string code)
    {
        Contract.ErrorRow[] matched = [.. rows.Where(row => string.Equals(row.Code, code, StringComparison.Ordinal))];
        return matched.Length == 1
            ? matched[0]
            : throw new InvalidOperationException(
                $"{BrowserContract} -> Errors has {matched.Length} rows with code '{code}'. "
                + "The composed suites read the status and the code from that table, keyed by code.");
    }
}

/// <summary>
/// One section of a contract file: the lines after its heading up to the next heading of any level,
/// with the file and the heading kept so every failure can name both.
/// </summary>
internal sealed class ContractSection
{
    private readonly string[] lines;

    private ContractSection(string file, string heading, string[] lines)
    {
        File = file;
        Heading = heading;
        this.lines = lines;
    }

    /// <summary>The contract file, relative to the repository root, as a failure names it.</summary>
    public string File { get; }

    /// <summary>The heading without its hashes, as a failure names it.</summary>
    public string Heading { get; }

    /// <summary>
    /// The section headed by the line <paramref name="heading"/> (hashes included), ending at the next
    /// heading line outside a fenced block. Throws naming the file and the heading when no line is it.
    /// </summary>
    public static ContractSection Of(string file, string text, string heading)
    {
        string[] all = [.. text.Split('\n').Select(line => line.TrimEnd('\r'))];
        string name = heading.TrimStart('#').Trim();
        int start = Array.FindIndex(all, line => string.Equals(line.Trim(), heading, StringComparison.Ordinal));
        if (start < 0)
        {
            throw new InvalidOperationException(
                $"{file} has no '{heading}' section. The contract reader reads {name} from there.");
        }

        List<string> body = [];
        bool fenced = false;
        foreach (string line in all.Skip(start + 1))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
            }
            else if (!fenced && line.StartsWith('#'))
            {
                break;
            }

            body.Add(line);
        }

        return new ContractSection(file, name, [.. body]);
    }

    /// <summary>The section's lines, joined.</summary>
    public string Text => string.Join('\n', lines);

    /// <summary>The section's top-level bullets, each without its <c>- </c>.</summary>
    public IEnumerable<string> Bullets() =>
        lines.Where(line => line.StartsWith("- ", StringComparison.Ordinal)).Select(line => line[2..]);

    /// <summary>The lines inside the section's first fenced block. A section with no closed fence throws.</summary>
    public IReadOnlyList<string> FencedBlock()
    {
        int open = Array.FindIndex(lines, line => line.TrimStart().StartsWith("```", StringComparison.Ordinal));
        int close = open >= 0 ? Array.FindIndex(lines, open + 1, line => line.TrimStart().StartsWith("```", StringComparison.Ordinal)) : -1;
        return close > open + 1 ? lines[(open + 1)..close] : throw Broken("no fenced block");
    }

    /// <summary>A failure that names the file and the section, saying what is missing from it.</summary>
    public InvalidOperationException Broken(string what) =>
        new($"{File} -> {Heading}: {what}. The contract reader reads it from there.");

    /// <summary>
    /// The data rows of every table in the section, in order, each as its trimmed cells: a table is a
    /// run of consecutive lines that begin <c>|</c>, and its header row and the separator row under
    /// it are not data. A run whose second line is not a separator is not a table and throws, rather
    /// than have its lines read as rows or skipped. A section with no table row throws.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows()
    {
        List<IReadOnlyList<string>> rows = [];
        List<string> table = [];
        foreach (string line in lines.Append(string.Empty))
        {
            if (line.StartsWith('|'))
            {
                table.Add(line);
                continue;
            }

            if (table.Count > 0)
            {
                rows.AddRange(table.Count >= 2 && Cells(table[1]).All(IsSeparator)
                    ? table.Skip(2).Select(Cells)
                    : throw Broken($"the table that begins '{table[0]}' has no separator row under its header"));
                table.Clear();
            }
        }

        return rows.Count > 0 ? rows : throw Broken("no table rows");
    }

    /// <summary>The backquoted names in <paramref name="text"/>, in order, without their backquotes.</summary>
    public static ImmutableArray<string> Backquoted(string text)
    {
        ImmutableArray<string>.Builder names = ImmutableArray.CreateBuilder<string>();
        string[] parts = text.Split('`');
        for (int index = 1; index < parts.Length - 1; index += 2)
        {
            names.Add(parts[index]);
        }

        return names.ToImmutable();
    }

    /// <summary>The one backquoted name in <paramref name="cell"/>; none or several throws naming <paramref name="what"/>.</summary>
    public string OneName(string cell, string what)
    {
        ImmutableArray<string> names = Backquoted(cell);
        return names.Length == 1 ? names[0] : throw Broken($"{what} '{cell}' holds {names.Length} backquoted names, not one");
    }

    private static IReadOnlyList<string> Cells(string row) =>
        [.. row.Trim().Trim('|').Split('|').Select(cell => cell.Trim())];

    private static bool IsSeparator(string cell) =>
        cell.Length > 0 && cell.Trim(':').Length > 0 && cell.Trim(':').All(c => c == '-');
}
