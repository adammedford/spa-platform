using System.Net;
using Gateway.TestHarness;
using Platform.Abstractions;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-27: "<c>GET</c> and <c>POST</c> to <c>/signout-oidc</c> with a live session -> 404. The session
/// survives." (docs/plan/04-tests.md.)
///
/// <para><b>What is proven here, and what is not.</b> The 404 on both methods is proven. "With a
/// live session" and "the session survives" are not: a session needs
/// <c>src/Platform.Identity</c>, which starts at <c>contracts-v1</c>, so the whole row joins this
/// class at G4 with one more test and no change to these two. That is not a weakening of the row as
/// it stands today — the path is refused at stage 6, before authentication runs at all
/// (<c>contracts/pipeline.md</c>), so no session state is reachable from this request whether one
/// exists or not. The reason the path is switched off is threat T18: the framework's remote sign-out
/// would let a provider-shaped request end a session from outside the browser.</para>
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
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Header("Content-Type"));
        Assert.Contains("\"code\":\"not_found\"", response.Body, StringComparison.Ordinal);

        // Nothing about the request in the body: the path is the caller's text.
        Assert.DoesNotContain("signout-oidc", response.Body, StringComparison.Ordinal);

        Assert.Equal(RuleIds.PathReserved, await Observed.RuleOfOneRequestAsync(Harness, before, Suite.Ct));
    }
}
