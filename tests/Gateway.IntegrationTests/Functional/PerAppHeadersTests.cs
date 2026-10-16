using System.Collections.Immutable;
using System.Net;
using System.Text.Json.Nodes;
using Gateway.TestHarness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Registry;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// F-06, first clause: "Read headers per app" -> "App-specific CSP" (docs/plan/04-tests.md). The
/// hermetic tier of task 6.2a.
///
/// <para>Two apps are read in one run and contrasted, because a single app's policy proven twice
/// would not show tenancy: the claim is that app 2's entry changes app 2's policy and nothing of
/// app 1's. The expected policy is the baseline in <c>contracts/http-contract.md</c> plus the
/// additions in <c>fixtures/apps/app2.json</c>, both read from those files
/// (<see cref="ContractText"/>).</para>
///
/// <para>These requests isolate the HTTP contract named above. Session establishment and
/// preservation require separate acceptance evidence; a successful response here proves only the
/// asserted routing, policy or telemetry behaviour.</para>
/// </summary>
public sealed class PerAppHeadersTests(PerAppHeadersTests.Cell cell) : IClassFixture<PerAppHeadersTests.Cell>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One harness for this class: nothing here changes the registry.</summary>
    public sealed class Cell : IAsyncLifetime
    {
        public GatewayHarness Harness { get; private set; } = null!;

        public async ValueTask InitializeAsync() =>
            Harness = await GatewayHarness.StartAsync(new GatewayHarnessOptions { SessionRequired = true }, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
    }

    private GatewayHarness Harness => cell.Harness;

    /// <summary>The issuer origin stage 5 splices into <c>form-action</c>, as the gateway reads it.</summary>
    private string IssuerBrowserOrigin =>
        Harness.Services.GetRequiredService<IConfiguration>()[$"Issuers:{Harness.Options.IssuerName}:BrowserOrigin"]!;

    [Fact]
    public async Task Each_app_is_served_the_baseline_plus_its_own_entrys_additions_and_no_other_apps()
    {
        using TestBrowser browser = await Harness.CreateSignedInBrowserAsync(Ct);

        BrowserResponse one = await browser.NavigateAsync(Harness.App("app1").Url("/"), ct: Ct);
        BrowserResponse two = await browser.NavigateAsync(Harness.App("app2").Url("/"), ct: Ct);

        // app1's entry carries no csp, so its policy is the contract's baseline exactly. app2's adds
        // data: to img-src, which is the fixture's own value, read from the fixture.
        string baseline = ContractText.BaselineCsp(IssuerBrowserOrigin);
        string app2Addition = FixtureFiles.AppDefinition("app2")["csp"]!["img-src"]!.AsArray().Single()!.GetValue<string>();

        Assert.Equal(baseline, one.Header("Content-Security-Policy"));
        Assert.Equal(
            baseline.Replace("img-src 'self'", "img-src 'self' " + app2Addition, StringComparison.Ordinal),
            two.Header("Content-Security-Policy"));
    }

    [Fact]
    public async Task The_two_policies_differ_only_where_the_entry_differs_and_agree_on_the_baseline()
    {
        using TestBrowser browser = await Harness.CreateSignedInBrowserAsync(Ct);

        BrowserResponse one = await browser.NavigateAsync(Harness.App("app1").Url("/"), ct: Ct);
        BrowserResponse two = await browser.NavigateAsync(Harness.App("app2").Url("/"), ct: Ct);

        ImmutableArray<KeyValuePair<string, string>> first = ContractText.Directives(one.Header("Content-Security-Policy")!);
        ImmutableArray<KeyValuePair<string, string>> second = ContractText.Directives(two.Header("Content-Security-Policy")!);

        // The contrast itself: the same directives in the same order, differing in exactly those the
        // entries differ in, and in no other.
        Assert.Equal(first.Select(directive => directive.Key), second.Select(directive => directive.Key));
        IEnumerable<string> differ = first.Zip(second)
            .Where(pair => pair.First.Value != pair.Second.Value)
            .Select(pair => pair.First.Key);
        Assert.Equal(DirectivesTheEntriesDiffer("app1", "app2"), differ);
    }

    [Fact]
    public async Task The_served_policy_names_exactly_the_baselines_ten_directives()
    {
        using TestBrowser browser = await Harness.CreateSignedInBrowserAsync(Ct);

        BrowserResponse served = await browser.NavigateAsync(Harness.App("app2").Url("/"), ct: Ct);

        // contracts/http-contract.md: "An entry's csp adds sources to these ten directives and to no
        // others". The policy the browser receives therefore names exactly the baseline's directives,
        // whatever an entry asked for.
        Assert.Equal(
            ContractText.Directives(ContractText.BaselineCspTemplate).Select(directive => directive.Key),
            ContractText.Directives(served.Header("Content-Security-Policy")!).Select(directive => directive.Key));
    }

    [Fact]
    public async Task The_fixed_header_set_is_on_both_apps_and_no_cross_origin_grant_is()
    {
        using TestBrowser browser = await Harness.CreateSignedInBrowserAsync(Ct);

        BrowserResponse one = await browser.NavigateAsync(Harness.App("app1").Url("/"), ct: Ct);
        BrowserResponse two = await browser.NavigateAsync(Harness.App("app2").Url("/"), ct: Ct);

        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(HttpStatusCode.OK, two.StatusCode);
        foreach (BrowserResponse response in (BrowserResponse[])[one, two])
        {
            foreach ((string name, string value) in ContractText.FixedResponseHeaders)
            {
                Assert.Equal(value, response.Header(name));
            }

            Assert.DoesNotContain(response.Headers.Keys, name =>
                name.StartsWith(ContractText.ForbiddenHeaderPrefix, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task An_entry_naming_a_directive_outside_the_sanctioned_set_is_refused_whole()
    {
        // Its own harness, because this test publishes a registry document and the class fixture's
        // does not. The input the clause above cannot supply: no app fixture names an out-of-set
        // directive, and fixtures/ is workstream 5's, so the entry arrives the way an operator's
        // would -- by publishing one (CODING_STANDARDS.md -> "An assertion must be able to fail").
        await using GatewayHarness harness = await GatewayHarness.StartAsync(new GatewayHarnessOptions { SessionRequired = true }, Ct);
        using TestBrowser browser = await harness.CreateSignedInBrowserAsync(Ct);

        BrowserResponse before = await browser.NavigateAsync(harness.App("app1").Url("/"), ct: Ct);
        RefreshOutcome outcome = await harness.UpdateRegistryAsync(
            document => document["apps"]!.AsArray().Single(app => app!["appId"]!.GetValue<string>() == "app1")!
                .AsObject()["csp"] = new JsonObject { ["worker-src"] = new JsonArray("'self'") },
            Ct);
        BrowserResponse after = await browser.NavigateAsync(harness.App("app1").Url("/"), ct: Ct);

        // contracts/registry.schema.json -> csp.propertyNames names exactly the baseline's ten, so a
        // document naming an eleventh fails validation and the live snapshot stays. Refused rather
        // than partially honoured: the policy the browser gets is the one it got before the publish,
        // and the baseline was not widened by it.
        Assert.Equal(RefreshOutcome.Rejected, outcome);
        Assert.Equal(before.Header("Content-Security-Policy"), after.Header("Content-Security-Policy"));
        Assert.DoesNotContain("worker-src", after.Header("Content-Security-Policy")!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The directives two entries' <c>csp</c> sections disagree about, read from the fixtures. This is
    /// what the two policies are allowed to differ in, and it is derived from the entries rather than
    /// written down beside them.
    /// </summary>
    private static IEnumerable<string> DirectivesTheEntriesDiffer(string left, string right)
    {
        static Dictionary<string, string> Additions(string appId) =>
            FixtureFiles.AppDefinition(appId)["csp"] is JsonObject csp
                ? csp.ToDictionary(
                    directive => directive.Key,
                    directive => string.Join(' ', directive.Value!.AsArray().Select(source => source!.GetValue<string>())),
                    StringComparer.Ordinal)
                : [];

        Dictionary<string, string> one = Additions(left);
        Dictionary<string, string> two = Additions(right);
        return ContractText.Directives(ContractText.BaselineCspTemplate)
            .Select(directive => directive.Key)
            .Where(directive => one.GetValueOrDefault(directive) != two.GetValueOrDefault(directive));
    }
}
