using System.Net;
using System.Text.Json.Nodes;
using Gateway.TestHarness;
using Platform.Registry;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-12, <b>partial</b>: "Upstream answers with <c>Set-Cookie</c>, a CORS header and its own CSP, on
/// a 200 and on a 500 -> The browser receives no cookie, no CORS header and the gateway's CSP, both
/// times." (docs/plan/04-tests.md.)
///
/// <para><b>What is proven: the CORS header and the policy, on both statuses.</b> Both are stage 5's
/// (task 4.5, landed): <c>contracts/http-contract.md</c> -> Response headers says the header set is
/// "Set on every response, including proxied ones, where they overwrite what the upstream sent", and
/// "No <c>Access-Control-*</c> header, ever. One sent by an upstream is stripped." Stage 5 applies
/// them from <c>Response.OnStarting</c>, so they land after YARP has copied the upstream's — and the
/// composed gateway is the only place that can be read, because workstream 4's own project has no
/// stage 10 and no upstream to be overwritten.</para>
///
/// <para><b>What is NOT proven, and why it is absent rather than failing: the <c>Set-Cookie</c>
/// clause.</b> Stripping it belongs to plan row <b>4.2</b> ("Strip <c>Set-Cookie</c> and
/// <c>Access-Control-*</c> from responses on every status, which in YARP needs
/// <c>ResponseCondition.Always</c>"), whose own needs are "4.1, G1b" — it has not landed, and
/// workstream 6 owns the proof and not the stage. Measured on this branch, an upstream's
/// <c>Set-Cookie</c> does reach the browser today. A test asserting the contract here would be red
/// on arrival for a row nobody has written yet, and a test asserting what happens today would have
/// to be deleted when 4.2 lands rather than kept. So the clause waits for 4.2, in this class,
/// against this upstream. The same applies to N-11 in full, which is why this suite has no N-11:
/// the caller's <c>Cookie</c>, bearer token and <c>X-CSRF-TOKEN</c> all reach an upstream on this
/// branch, and stripping them is the same row 4.2.</para>
///
/// <para><b>Temporal caveat.</b> The calls below reach an upstream with no session because the shell
/// gate (stage 8, workstream 3) is not in the pipeline yet; when it lands they become 401s
/// (ADR-0008) and this class needs a session to reach an upstream at all. The header claims are
/// stage 5's and survive that; the 200 and the 500 are what is to be re-read.</para>
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

        // The app's own policy, read from the app's own shell: "the gateway's CSP" is whatever this
        // app is served, so it is taken from a response the upstream had nothing to do with rather
        // than rebuilt here.
        BrowserResponse shell = await browser.NavigateAsync(harness.App("app1").Url("/"), ct: Suite.Ct);
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
