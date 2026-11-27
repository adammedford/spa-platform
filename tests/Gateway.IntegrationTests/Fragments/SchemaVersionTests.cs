using System.Net;
using System.Text.Json;
using Gateway.TestHarness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>
/// F-30 (#1007, part of #961 -> "Composition"): a <c>schemaVersion: 1</c> document, the fixtures' two
/// apps with no fragment anywhere, is served by the fragments build as it was served before fragments:
/// every app's shell, its <c>/bff</c> and its <c>/api</c> answer, <c>/bff/session</c> reads an empty
/// <c>fragments</c>, and each host carries the policy the baseline and its own entry make, with nothing a
/// fragment adds.
/// </summary>
public sealed class SchemaVersionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task G9_a_schemaVersion_1_document_is_served_unchanged_by_the_fragments_build()
    {
        // Mutation that turns it red: a validator that requires schemaVersion 2 of every document rejects
        // the first revision, so the harness never starts. Mutation: a policy merge that adds anything for
        // a host with no fragments (an empty fragment's directive, say) changes app1's policy from the
        // baseline. Mutation: /bff/session omitting fragments for an app with none fails the {} check.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        Assert.Equal(1, harness.RegistryDocument["schemaVersion"]!.GetValue<int>());
        using (JsonDocument stored = JsonDocument.Parse((await SpactlRuns.TextAsync(harness, GatewayHarness.RegistryKey, Ct))!))
        {
            Assert.Equal(1, stored.RootElement.GetProperty("schemaVersion").GetInt32());
        }

        Assert.All(harness.Registry.Current.ByAppId.Values, app => Assert.Empty(app.Fragments));

        using TestBrowser browser = await harness.CreateSignedInBrowserAsync(Ct);
        string issuerOrigin = harness.Services.GetRequiredService<IConfiguration>()[$"Issuers:{harness.Options.IssuerName}:BrowserOrigin"]!;
        string baseline = Contract.BaselineCsp(issuerOrigin);
        string app2Addition = FixtureFiles.AppDefinition("app2")["csp"]!["img-src"]!.AsArray().Single()!.GetValue<string>();
        Dictionary<string, (string Api, string Policy)> expected = new(StringComparer.Ordinal)
        {
            ["app1"] = ("orders", baseline),
            ["app2"] = ("reports", baseline.Replace("img-src 'self'", "img-src 'self' " + app2Addition, StringComparison.Ordinal)),
        };

        foreach ((string appId, (string api, string policy)) in expected)
        {
            HarnessApp app = harness.App(appId);
            BrowserResponse shell = await browser.NavigateAsync(app.Url("/"), ct: Ct);
            Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
            Assert.Equal(policy, shell.Header("Content-Security-Policy"));

            BrowserResponse session = await browser.GetAsync(app.Url("/bff/session").ToString(), ct: Ct);
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
            Assert.Equal(policy, session.Header("Content-Security-Policy"));
            using (JsonDocument body = JsonDocument.Parse(session.Body))
            {
                Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("fragments").ValueKind);
                Assert.Empty(body.RootElement.GetProperty("fragments").EnumerateObject());
            }

            BrowserResponse whoami = await browser.GetAsync(app.Url($"/api/{api}/whoami").ToString(), ct: Ct);
            Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
            using JsonDocument who = JsonDocument.Parse(whoami.Body);
            Assert.Equal(app.ClientId, who.RootElement.GetProperty("clientId").GetString());
        }

        // No line of a request on these hosts names a fragment.
        Assert.DoesNotContain(harness.Log.Json, line => line.TryGetProperty("fragment_id", out _));
    }
}
