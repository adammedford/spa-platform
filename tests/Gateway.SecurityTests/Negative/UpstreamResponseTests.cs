using System.Net;
using System.Text.Json.Nodes;
using Gateway.TestHarness;
using Platform.Registry;
using Platform.Testing;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-12: "Upstream answers with <c>Set-Cookie</c>, a CORS header and its own CSP, on
/// a 200 and on a 500 -> The browser receives no cookie, no CORS header and the gateway's CSP, both
/// times." (docs/plan/04-tests.md.)
///
/// <para><b>What is proven: the CORS header and the policy, on both statuses.</b> Both are UsePlatformSecurityHeaders'
/// (task 4.5): <c>contracts/http-contract.md</c> -> Response headers says the header set is
/// "Set on every response, including proxied ones, where they overwrite what the upstream sent", and
/// "No <c>Access-Control-*</c> header, ever. One sent by an upstream is stripped." UsePlatformSecurityHeaders applies
/// them from <c>Response.OnStarting</c>, so they land after YARP has copied the upstream's — and the
/// composed gateway is the only place that can be read, because workstream 4's own project has no
/// proxy endpoint and no upstream to be overwritten.</para>
///
/// <para><b>Every call here carries a session</b>, because that is the only call an upstream can
/// answer: the proxy's token step refuses a sessionless one before it is forwarded. The
/// <c>Set-Cookie</c> clause is held by the browser that signed in. The proxy strips the header on
/// every status (task 4.2, T-C7); the browser's jar is read as well as the response, because the
/// row's words are "the browser receives no cookie".</para>
/// </summary>
public sealed class UpstreamResponseTests
{
    /// <summary>The API name the hostile upstream is listed under, for this test only.</summary>
    private const string ApiName = "hostile";

    [Theory]
    [InlineData(200)]
    [InlineData(500)]
    public async Task An_upstreams_cors_header_and_policy_are_replaced_by_the_gateways_on_any_status(int status)
    {
        await using HostileUpstream upstream = HostileUpstream.Start();
        upstream.Status = status;

        await using GatewayHarness harness = await StartWith(upstream);
        using TestBrowser browser = harness.CreateBrowser();

        // The app's own policy, read from the shell the login lands on: "the gateway's CSP" is
        // whatever this app is served, so it is taken from a response the upstream had nothing to do
        // with rather than rebuilt here.
        BrowserResponse shell = await harness.SignInAsync(browser, "app1", Suite.Ct);
        string gatewayPolicy = Assert.IsType<string>(shell.Header("Content-Security-Policy"));

        BrowserResponse proxied = await browser.GetAsync($"/api/{ApiName}/thing", ct: Suite.Ct);

        // The upstream's own answer did come back, so the assertions below are about a response the
        // upstream really wrote: a gateway that had refused the request would satisfy them trivially
        // (CODING_STANDARDS.md -> "An assertion must be able to fail").
        Assert.Equal(status, (int)proxied.StatusCode);
        Assert.Equal(upstream.Body, proxied.Body);

        // No cross-origin grant survived, on either status.
        Assert.DoesNotContain(
            proxied.Headers.Keys,
            name => name.StartsWith(Contract.ForbiddenHeaderPrefix, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(HostileUpstream.PlantedCorsOrigin, string.Join(" ", proxied.Headers.SelectMany(
            header => header.Value.Select(value => header.Key + ": " + value))), StringComparison.Ordinal);

        // One policy, and it is the app's, not the upstream's.
        Assert.Equal(gatewayPolicy, proxied.Header("Content-Security-Policy"));
        Assert.DoesNotContain(HostileUpstream.PlantedPolicy, proxied.Header("Content-Security-Policy")!, StringComparison.Ordinal);
        Assert.Single(proxied.Headers["Content-Security-Policy"]);

        // And the rest of the fixed set is on it too, which is the same claim the contract makes in
        // one sentence about "every response, including proxied ones".
        foreach ((string name, string value) in Contract.FixedResponseHeaders)
        {
            Assert.Equal(value, proxied.Header(name));
        }
    }

    [Theory]
    [InlineData(200)]
    [InlineData(500)]
    public async Task An_upstreams_set_cookie_never_reaches_a_signed_in_browser_on_any_status(int status)
    {
        await using HostileUpstream upstream = HostileUpstream.Start();
        upstream.Status = status;

        await using GatewayHarness harness = await StartWith(upstream);
        HarnessApp app = harness.App("app1");
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        string[] before = [.. browser.AllCookies.Select(cookie => cookie.Name + "=" + cookie.Value)];

        BrowserResponse proxied = await browser.GetAsync($"/api/{ApiName}/thing", ct: Suite.Ct);

        // The upstream's own answer, so the absence below is about a response it really wrote.
        Assert.Equal(status, (int)proxied.StatusCode);
        Assert.Equal(upstream.Body, proxied.Body);

        Assert.Empty(proxied.SetCookies);
        Assert.DoesNotContain(browser.AllCookies, cookie => cookie.Name == HostileUpstream.PlantedCookie);
        Assert.Equal(before, browser.AllCookies.Select(cookie => cookie.Name + "=" + cookie.Value));
        Assert.True(SignedIn.HoldsSessionCookie(browser, app));
    }

    [Fact]
    public async Task The_upstream_really_does_send_what_the_gateway_has_to_overwrite()
    {
        // The guard on the guard. Every assertion above is about headers not arriving, so if this
        // upstream ever stopped sending them the whole class would pass while proving nothing. This
        // reads the upstream directly, with no gateway in the way, and holds it to sending all three.
        await using HostileUpstream upstream = HostileUpstream.Start();

        RawResponse direct = await RawHttp.SendAsync(upstream.Address, "GET", "/thing", "127.0.0.1", ct: Suite.Ct);

        Assert.Equal(200, direct.StatusCode);
        Assert.Contains(HostileUpstream.PlantedCookie, Assert.Single(direct.Values("Set-Cookie")), StringComparison.Ordinal);
        Assert.Equal(HostileUpstream.PlantedCorsOrigin, Assert.Single(direct.Values("Access-Control-Allow-Origin")));
        Assert.Equal(HostileUpstream.PlantedPolicy, Assert.Single(direct.Values("Content-Security-Policy")));
    }

    /// <summary>
    /// A cell whose app 1 lists <paramref name="upstream"/> under <see cref="ApiName"/>.
    ///
    /// <para>Two things have to be arranged together, which is why this is a helper and not a class
    /// fixture. The upstream's port is only known once it is listening, and
    /// <c>Policy:AllowedUpstreams</c> is read when the gateway starts, so the listener comes first
    /// and the cell is configured around it. The registry entry is then published the way an
    /// operator's would be — the validator refuses an upstream the policy does not name, so a
    /// mistake in either half shows up as a rejected document rather than as a passing test.</para>
    /// </summary>
    private static async Task<GatewayHarness> StartWith(HostileUpstream upstream)
    {
        // The harness lists one allowed upstream per distinct API origin it set up. With no echo API
        // there are two, at indices 0 and 1, so this adds a third rather than overwriting one.
        GatewayHarnessOptions options = new()
        {
            EchoApi = null,
            Configuration = new Dictionary<string, string?>
            {
                ["Policy:AllowedUpstreams:2"] = upstream.Address.GetLeftPart(UriPartial.Authority),
            },
        };

        GatewayHarness harness = await Suite.StartAsync(options, Suite.Ct);
        try
        {
            RefreshOutcome outcome = await harness.UpdateRegistryAsync(
                document => document["apps"]!.AsArray()
                    .Single(app => app!["appId"]!.GetValue<string>() == "app1")!
                    .AsObject()["apis"]!.AsArray()
                    .Add(new JsonObject
                    {
                        ["name"] = ApiName,

                        // Scheme and authority only: registry.schema.json's upstream pattern allows
                        // no path, so the absolute URI's trailing slash would fail the shape check
                        // before the policy rule was ever reached.
                        ["upstream"] = upstream.Address.GetLeftPart(UriPartial.Authority),
                        ["timeoutSeconds"] = 30,
                    }),
                Suite.Ct);

            Assert.Equal(RefreshOutcome.Swapped, outcome);
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }
}
