using Gateway.TestHarness;

namespace Gateway.IntegrationTests.Harness;

/// <summary>
/// The contract reader's parsers (<see cref="ContractParser"/>), fed planted text through the internal
/// seam the harness opens to this project, so each read is known to fail on the shape that breaks it
/// before every composed suite asserts against what it returns from the live contract
/// (CODING_STANDARDS.md -> "An assertion must be able to fail": a rule over a model is fed a breaking
/// input). Each mutation test names the edit to the contract it plants, and each has a control beside
/// it that reads the same text unbroken, so the throw is the mutation's and not the planted text's.
///
/// <para>The expected values are the planted text's own, written here as literals: the text is the
/// test's, not the contract's, so the literals repeat nothing the contract holds (#139, Testing
/// Decisions).</para>
/// </summary>
public sealed class ContractTests
{
    private const string Errors = """
        # Browser contract

        ## Errors

        Errors are `application/problem+json` with a stable `code`.

        | Status | `code` | When |
        | --- | --- | --- |
        | 401 | `unauthenticated` | No session |
        | 403 | `csrf_failed` | The antiforgery token was missing or wrong |

        One refusal is not a problem body.
        """;

    [Fact]
    public void An_errors_row_is_found_by_its_code_cell_with_its_status_and_when_text()
    {
        Contract.ErrorRow row = ContractParser.Error(ContractParser.ErrorRows(Errors), "csrf_failed");

        Assert.Equal(new Contract.ErrorRow(403, "csrf_failed", "The antiforgery token was missing or wrong"), row);
    }

    [Fact]
    public void The_errors_heading_removed_throws_naming_the_file_and_the_section()
    {
        string planted = Errors.Replace("## Errors", "## Problems", StringComparison.Ordinal);

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => ContractParser.ErrorRows(planted));

        Assert.Contains("contracts/http-contract.md", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Errors", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_rows_with_one_code_make_the_lookup_throw_naming_the_table()
    {
        string planted = Errors.Replace(
            "| 401 | `unauthenticated` |", "| 401 | `csrf_failed` |", StringComparison.Ordinal);

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => ContractParser.Error(ContractParser.ErrorRows(planted), "csrf_failed"));

        Assert.Contains("contracts/http-contract.md -> Errors", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_code_the_table_does_not_hold_throws_naming_the_table_and_a_fragment_of_the_prose_is_not_a_code()
    {
        var rows = ContractParser.ErrorRows(Errors);

        InvalidOperationException absent = Assert.Throws<InvalidOperationException>(() => ContractParser.Error(rows, "not_found"));
        Assert.Contains("contracts/http-contract.md -> Errors", absent.Message, StringComparison.Ordinal);
        Assert.Contains("not_found", absent.Message, StringComparison.Ordinal);

        // The key is the code cell, exactly: the "When" prose and a prefix of a code both miss.
        Assert.Throws<InvalidOperationException>(() => ContractParser.Error(rows, "No session"));
        Assert.Throws<InvalidOperationException>(() => ContractParser.Error(rows, "csrf"));
    }

    private const string Cookies = """
        ## Cookies

        | Cookie | Attributes | Holds |
        | --- | --- | --- |
        | `__Host-spa-session` | `Secure; HttpOnly; SameSite=Lax; Path=/`, no `Domain` | A session id |
        | `__Host-spa-corr.*` and `__Host-spa-nonce.*` | `Secure; HttpOnly`, minutes | Login state |

        Every cookie carries the `__Host-` prefix.

        ## Request headers the SPA sends
        """;

    [Fact]
    public void The_cookie_names_are_the_first_cells_of_the_rows_in_order_with_a_wildcard_keeping_its_star()
    {
        Assert.Equal(
            ["__Host-spa-session", "__Host-spa-corr.*", "__Host-spa-nonce.*"],
            ContractParser.CookieNames(Cookies));
    }

    [Fact]
    public void A_cookies_section_with_prose_but_no_rows_throws_because_the_bare_prefix_is_not_a_cookie()
    {
        string planted = string.Join('\n', Cookies.Split('\n').Where(line => !line.StartsWith('|')));

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => ContractParser.CookieNames(planted));

        Assert.Contains("contracts/http-contract.md -> Cookies", thrown.Message, StringComparison.Ordinal);
    }

    private const string Baseline = """
        ### The baseline content security policy

        ```text
        default-src 'self'; script-src 'self';
        form-action 'self' <identity provider origin>
        ```

        The provider origin is `Issuers:{name}:BrowserOrigin`.
        """;

    [Fact]
    public void The_baseline_is_its_fenced_block_on_one_line_and_the_splice_replaces_the_one_placeholder()
    {
        string template = ContractParser.BaselineCspTemplate(Baseline);

        Assert.Equal("default-src 'self'; script-src 'self'; form-action 'self' <identity provider origin>", template);
        Assert.Equal(
            "default-src 'self'; script-src 'self'; form-action 'self' http://127.0.0.1:7001",
            ContractParser.BaselineCsp(template, "http://127.0.0.1:7001"));
    }

    [Fact]
    public void The_baseline_block_unfenced_throws_naming_the_file_and_the_section()
    {
        string planted = Baseline.Replace("```text\n", "", StringComparison.Ordinal).Replace("```\n", "", StringComparison.Ordinal);

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => ContractParser.BaselineCspTemplate(planted));

        Assert.Contains("contracts/http-contract.md -> The baseline content security policy", thrown.Message, StringComparison.Ordinal);
    }

    private const string ResponseHeaders = """
        ## Response headers

        - `Content-Security-Policy`: the baseline below plus the entry's additions
        - `X-Content-Type-Options: nosniff`
        - `Strict-Transport-Security: max-age=31536000`
        - No `Access-Control-*` header, ever. One sent by an upstream is stripped.

        ### The baseline content security policy
        """;

    [Fact]
    public void The_fixed_headers_are_the_name_value_bullets_and_the_forbidden_prefix_is_read_from_its_bullet()
    {
        // The policy bullet names no value inside its backquotes, so it is not a fixed header.
        Assert.Equal(
            ["Strict-Transport-Security: max-age=31536000", "X-Content-Type-Options: nosniff"],
            ContractParser.FixedResponseHeaders(ResponseHeaders)
                .Select(header => $"{header.Key}: {header.Value}")
                .Order(StringComparer.Ordinal));
        Assert.Equal("Access-Control-", ContractParser.ForbiddenHeaderPrefix(ResponseHeaders));
    }

    [Fact]
    public void The_forbidding_bullet_removed_throws_naming_the_file_and_the_section()
    {
        string planted = string.Join('\n', ResponseHeaders.Split('\n').Where(line => !line.StartsWith("- No ", StringComparison.Ordinal)));

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => ContractParser.ForbiddenHeaderPrefix(planted));

        Assert.Contains("contracts/http-contract.md -> Response headers", thrown.Message, StringComparison.Ordinal);
    }

    private const string Telemetry = """
        # Telemetry

        | Kind | Names |
        | --- | --- |
        | Log fields | `ts`, `level`, `msg` |
        | `route` values | `asset`, `api` |
        | Metrics | `gateway.requests` |

        ## How `rule` and `session_ref` reach the request line
        """;

    [Fact]
    public void A_telemetry_row_is_the_backquoted_names_of_its_second_cell_in_order()
    {
        Assert.Equal(["ts", "level", "msg"], ContractParser.TelemetryNames(Telemetry, "Log fields"));
        Assert.Equal(["asset", "api"], ContractParser.TelemetryNames(Telemetry, "`route` values"));
    }

    [Fact]
    public void A_telemetry_row_renamed_throws_naming_the_file_and_the_row()
    {
        string planted = Telemetry.Replace("| Log fields |", "| Log keys |", StringComparison.Ordinal);

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => ContractParser.TelemetryNames(planted, "Log fields"));

        Assert.Contains("contracts/telemetry.md", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Log fields", thrown.Message, StringComparison.Ordinal);
    }
}
