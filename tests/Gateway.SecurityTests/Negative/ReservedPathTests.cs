using System.Net;
using Gateway.TestHarness;
using Platform.Abstractions;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-27: "<c>GET</c> and <c>POST</c> to <c>/signout-oidc</c> with a live session -> 404. The session
/// survives." (docs/plan/04-tests.md.)
///
/// <para>These requests prove the 404 for both methods. They establish no session and therefore
/// do not prove session survival. Threat T18 motivates both clauses: provider-shaped remote-signout
/// requests must not end a browser's session (docs/plan/04-tests.md, N-27).</para>
///
/// <para>The refusal is the request gate's reserved row, so it carries
/// <see cref="RuleIds.PathReserved"/> and the body's <c>code</c> is <c>not_found</c>
/// (<c>contracts/request-gate.md</c>; <c>contracts/http-contract.md</c> -> Errors). Both are
/// asserted, because the contract says tests assert on both.</para>
/// </summary>
public sealed class ReservedPathTests(ReservedPathTests.Cell cell) : IClassFixture<ReservedPathTests.Cell>
{
    /// <summary>One harness for this class: nothing here changes the registry or the trusted networks.</summary>
    public sealed class Cell : IAsyncLifetime
    {
        public GatewayHarness Harness { get; private set; } = null!;

        public async ValueTask InitializeAsync() =>
            Harness = await Suite.StartAsync(ct: TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
    }

    private GatewayHarness Harness => cell.Harness;

    [Fact]
    public async Task A_navigation_to_signout_oidc_is_404_under_path_reserved()
    {
        using TestBrowser browser = Harness.CreateBrowser();
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        BrowserResponse response = await browser.NavigateAsync(
            Harness.App("app1").Url("/signout-oidc"), ct: Suite.Ct);

        await AssertReservedAsync(response, before);
    }

    [Fact]
    public async Task A_form_post_to_signout_oidc_is_404_under_path_reserved()
    {
        using TestBrowser browser = Harness.CreateBrowser();

        // A page first, so the POST is a navigation the page started and carries the Origin and
        // Sec-Fetch-Site a browser would send. The reserved row answers before either is judged,
        // which is the point: a provider-shaped POST gets no further than a GET does.
        await browser.NavigateAsync(Harness.App("app1").Url("/"), ct: Suite.Ct);
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        BrowserResponse response = await browser.SubmitFormAsync("/signout-oidc", ct: Suite.Ct);

        await AssertReservedAsync(response, before);
    }

    private async Task AssertReservedAsync(BrowserResponse response, int before)
    {
        Contract.ErrorRow expected = Contract.Error("a reserved path that is switched off");
        Assert.Equal((HttpStatusCode)expected.Status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Header("Content-Type"));
        Assert.Contains($"\"code\":\"{expected.Code}\"", response.Body, StringComparison.Ordinal);

        // Nothing about the request in the body: the path is the caller's text.
        Assert.DoesNotContain("signout-oidc", response.Body, StringComparison.Ordinal);

        Assert.Equal(RuleIds.PathReserved, await Observed.RuleOfOneRequestAsync(Harness, before, Suite.Ct));
    }
}
