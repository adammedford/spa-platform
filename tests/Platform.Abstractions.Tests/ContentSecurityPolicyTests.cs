using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Abstractions.Tests;

/// <summary>
/// The one policy a host sends (#988 part 9; the fragments spec, #961 → "The write check"). The
/// expected headers are what Platform.Security's <c>SecurityHeaders</c> sends on main for the same
/// entry, copied from tests/Platform.Security.Tests/SecurityHeadersTests.cs, so the move cannot change
/// the header for an entry with no fragments.
/// </summary>
public sealed class ContentSecurityPolicyTests
{
    private const string Origin = "https://login.idp.test";

    private const string Baseline =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; "
        + "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none'; "
        + "form-action 'self' https://login.idp.test";

    [Theory]
    [InlineData("app1.json", Baseline)]
    [InlineData(
        "app2.json",
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; "
        + "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none'; "
        + "form-action 'self' https://login.idp.test")]
    public void An_entry_with_no_fragments_merges_to_the_header_main_sends_for_the_fixture(string fixture, string expected)
    {
        AppDefinition shell = TestApps.Entry(csp: FixtureCsp(fixture));

        MergedContentSecurityPolicy merged = ContentSecurityPolicy.Merge(shell, Origin);

        Assert.Equal(expected, merged.HeaderValue());
        Assert.Empty(merged.Dropped);
    }

    [Fact]
    public void Additions_follow_the_baseline_sources_as_main_sends_them()
    {
        // The same entry as SecurityHeadersTests' F06 two-apps case, and its expected header.
        AppDefinition shell = TestApps.Entry(csp: Csp(
            ("img-src", ["data:"]),
            ("connect-src", ["https://telemetry.example.test", "wss://live.example.test"]),
            ("form-action", ["https://pay.example.test"])));

        Assert.Equal(
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; "
            + "connect-src 'self' https://telemetry.example.test wss://live.example.test; frame-ancestors 'none'; "
            + "base-uri 'self'; object-src 'none'; form-action 'self' https://login.idp.test https://pay.example.test",
            ContentSecurityPolicy.Merge(shell, Origin).HeaderValue());
    }

    [Fact]
    public void An_addition_to_a_none_directive_replaces_none()
    {
        AppDefinition shell = TestApps.Entry(csp: Csp(("frame-ancestors", ["https://portal.example.test"])));

        Assert.Equal(
            ["https://portal.example.test"],
            ContentSecurityPolicy.Merge(shell, Origin).SourcesOf("frame-ancestors"));
    }

    [Fact]
    public void A_none_directive_whose_every_addition_is_dropped_keeps_none()
    {
        AppDefinition shell = TestApps.Entry(csp: Csp(("object-src", ["https://a.test;script-src"])));

        MergedContentSecurityPolicy merged = ContentSecurityPolicy.Merge(shell, Origin);

        Assert.Equal(["'none'"], merged.SourcesOf("object-src"));
        Assert.Equal([((string?)null, "object-src")], merged.Dropped);
    }

    [Fact]
    public void A_source_repeating_the_baseline_is_sent_once()
    {
        // The deliberate change (#988 decision, item 3): main sends script-src 'self' 'self'.
        // Mutation: drop AddOnce's check.
        AppDefinition shell = TestApps.Entry(csp: Csp(("script-src", ["'self'"]), ("form-action", [Origin])));

        MergedContentSecurityPolicy merged = ContentSecurityPolicy.Merge(shell, Origin);

        Assert.Equal(["'self'"], merged.SourcesOf("script-src"));
        Assert.Equal(["'self'", Origin], merged.SourcesOf("form-action"));
        Assert.Contains("; script-src 'self'; ", merged.HeaderValue(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_order_is_baseline_then_shell_then_fragments_in_registry_order()
    {
        // Mutation: walk the fragments in reverse, or put the shell's additions after them.
        AppDefinition shell = TestApps.Entry(
            csp: Csp(("script-src", ["https://shell.test"])),
            fragments:
            [
                TestApps.Fragment("b-ui", csp: Csp(("script-src", ["https://b.test"]))),
                TestApps.Fragment("a-ui", csp: Csp(("script-src", ["https://a.test"]))),
            ]);

        Assert.Equal(
            ["'self'", "https://shell.test", "https://b.test", "https://a.test"],
            ContentSecurityPolicy.Merge(shell, Origin).SourcesOf("script-src"));
    }

    [Fact]
    public void A_source_both_the_shell_and_a_fragment_add_appears_once_at_the_shells_position()
    {
        AppDefinition shell = TestApps.Entry(
            csp: Csp(("connect-src", ["https://shared.test", "https://shell.test"])),
            fragments:
            [
                TestApps.Fragment("orders-ui", csp: Csp(("connect-src", ["https://orders.test", "https://shared.test"]))),
            ]);

        Assert.Equal(
            ["'self'", "https://shared.test", "https://shell.test", "https://orders.test"],
            ContentSecurityPolicy.Merge(shell, Origin).SourcesOf("connect-src"));
    }

    [Fact]
    public void A_fragments_addition_outside_its_five_directives_is_not_sent_and_not_listed()
    {
        // Mutation: read every directive of a fragment's csp, not only FragmentDirectives.
        AppDefinition shell = TestApps.Entry(fragments:
        [
            TestApps.Fragment("orders-ui", csp: Csp(
                ("object-src", ["https://plugin.test"]),
                ("default-src", ["https://any.test"]),
                ("img-src", ["https://img.test"]))),
        ]);

        MergedContentSecurityPolicy merged = ContentSecurityPolicy.Merge(shell, Origin);

        Assert.Equal(["'none'"], merged.SourcesOf("object-src"));
        Assert.Equal(["'self'"], merged.SourcesOf("default-src"));
        Assert.Equal(["'self'", "https://img.test"], merged.SourcesOf("img-src"));
        Assert.Empty(merged.Dropped);
    }

    [Theory]
    [InlineData("https://a.test;script-src")]
    [InlineData("https://a.test,https://b.test")]
    [InlineData("https://a.test https://b.test")]
    [InlineData("https://a.test\u0001")]
    [InlineData("")]
    public void A_source_that_would_split_its_directive_is_dropped_and_named_by_its_fragment(string source)
    {
        // Mutation: splice the source as written, or record the shell (null) for a fragment's source.
        AppDefinition shell = TestApps.Entry(fragments:
        [
            TestApps.Fragment("orders-ui", csp: Csp(("script-src", [source, "https://kept.test"]))),
        ]);

        MergedContentSecurityPolicy merged = ContentSecurityPolicy.Merge(shell, Origin);

        Assert.Equal(["'self'", "https://kept.test"], merged.SourcesOf("script-src"));
        Assert.Equal([((string?)"orders-ui", "script-src")], merged.Dropped);
    }

    [Fact]
    public void Every_baseline_directive_is_sent_in_the_contracts_order_and_no_other()
    {
        AppDefinition shell = TestApps.Entry(csp: Csp(("worker-src", ["https://w.test"])));

        MergedContentSecurityPolicy merged = ContentSecurityPolicy.Merge(shell, Origin);

        // Mutation: swap two directives in ContentSecurityPolicy.Baseline. The contract's order is read
        // from contracts/http-contract.md, not from the array Merge walks.
        Assert.Equal(
            ContractTables.BaselineCspDirectives(),
            merged.Directives.Select(d => d.Directive));
        Assert.Empty(merged.SourcesOf("worker-src"));
    }

    [Fact]
    public void FragmentDirectives_are_the_contracts_fragment_directives_in_its_order()
    {
        // Mutation: change a directive in contracts/http-contract.md's "A fragment's `csp` adds sources
        // to" sentence alone, or in FragmentDirectives alone. The contract's list is read from the file.
        Assert.Equal(ContractTables.FragmentCspDirectives(), ContentSecurityPolicy.FragmentDirectives);
    }

    [Fact]
    public void The_fragment_directive_sentence_is_read_up_to_only()
    {
        // Mutation: read past "only", and the same sentence's `script-src` and `'strict-dynamic'` join;
        // anchor on "adds sources to" alone, and the entry sentence's `csp` is read instead.
        string[] lines =
        [
            "An entry's `csp` adds sources to these ten directives and to no others, `entry`.",
            "A fragment's `csp` adds sources to `a-src` and",
            "`b-src` only, and its `script-src` carries no `'strict-dynamic'`.",
        ];

        Assert.Equal(["a-src", "b-src"], ContractTables.ParseFragmentCspDirectives(lines));
    }

    [Fact]
    public void A_missing_fragment_directive_sentence_cannot_pass_as_no_directives()
    {
        // Mutation: drop the parser's anchor check, its closing check or its Assert.NotEmpty.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ContractTables.ParseFragmentCspDirectives(["A fragment adds `a-src` only."]));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ContractTables.ParseFragmentCspDirectives(
            ["A fragment's `csp` adds sources to `a-src`."]));
        Assert.Throws<Xunit.Sdk.NotEmptyException>(() => ContractTables.ParseFragmentCspDirectives(
            ["A fragment's `csp` adds sources to nothing only."]));
    }

    private static ImmutableDictionary<string, ImmutableArray<string>> Csp(
        params (string Directive, string[] Sources)[] directives) =>
        directives.ToImmutableDictionary(d => d.Directive, d => d.Sources.ToImmutableArray(), StringComparer.Ordinal);

    /// <summary>The <c>csp</c> of a fixture under fixtures/apps/, empty when it names none.</summary>
    private static ImmutableDictionary<string, ImmutableArray<string>> FixtureCsp(
        string fixture, [CallerFilePath] string? thisFile = null)
    {
        string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "fixtures", "apps", fixture));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("csp", out JsonElement csp))
        {
            return ImmutableDictionary<string, ImmutableArray<string>>.Empty;
        }

        return csp.EnumerateObject().ToImmutableDictionary(
            directive => directive.Name,
            directive => directive.Value.EnumerateArray().Select(source => source.GetString()!).ToImmutableArray(),
            StringComparer.Ordinal);
    }
}
