using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.TestHarness;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Registry;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// F-06, fourth clause: "switch release and switch back" -> "Content changes with no deployment"
/// (docs/plan/04-tests.md), through the composed gateway. Task 6.2a.
///
/// <para>The switch is driven the way an operator drives it: a second release is written into the
/// bucket under its own immutable prefix (ADR-0009), and then a registry document naming it is
/// published. Nothing in the gateway is asked to reload, and no test calls into a stage.</para>
///
/// <para><b>"No deployment" is asserted as an invariant, not narrated.</b> The deployed tier of this
/// row reads the task id and start time and finds them unchanged; in process, the equivalents are the
/// server instance, the service provider and the registry poll interval, and the test holds all three
/// across the switch. Without that, "with no deployment" would rest on the test not having restarted
/// anything, which is not an observation.</para>
///
/// <para>Each test starts its own harness, because each changes the registry.</para>
/// </summary>
public sealed class ReleaseSwitchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The second release's own prefix. A release is written once and never edited (ADR-0009).</summary>
    private const string SecondRelease = "2026-10-11.2";

    [Fact]
    public async Task Publishing_a_registry_document_switches_the_live_release_and_switching_back_restores_it()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser browser = harness.CreateBrowser();

        string first = harness.Options.Release;
        IReadOnlyDictionary<string, byte[]> firstFiles = FixtureFiles.Spa("app1");
        IReadOnlyDictionary<string, byte[]> secondFiles = NextRelease("app1", SecondRelease);
        await harness.PublishReleaseAsync("app1", SecondRelease, secondFiles, Ct);

        BrowserResponse before = await browser.NavigateAsync(harness.App("app1").Url("/"), ct: Ct);
        RefreshOutcome forward = await harness.UpdateRegistryAsync(document => Release(document, "app1", SecondRelease), Ct);
        BrowserResponse after = await browser.NavigateAsync(harness.App("app1").Url("/"), ct: Ct);
        RefreshOutcome back = await harness.UpdateRegistryAsync(document => Release(document, "app1", first), Ct);
        BrowserResponse restored = await browser.NavigateAsync(harness.App("app1").Url("/"), ct: Ct);

        Assert.Equal(RefreshOutcome.Swapped, forward);
        Assert.Equal(RefreshOutcome.Swapped, back);

        // The release the registry names is the prefix the bytes come from, and the rollback is as
        // cheap as the rollout: the original bytes are served again.
        Assert.Equal(firstFiles["index.html"], before.Bytes);
        Assert.Equal(secondFiles["index.html"], after.Bytes);
        Assert.Equal(firstFiles["index.html"], restored.Bytes);
        Assert.NotEqual(before.Bytes, after.Bytes);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task The_switch_changes_content_with_no_restart_and_no_new_service_provider()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser browser = harness.CreateBrowser();
        IConfiguration configuration = harness.Services.GetRequiredService<IConfiguration>();

        IServiceProvider services = harness.Services;
        IServer server = harness.Services.GetRequiredService<IServer>();
        Uri address = harness.GatewayAddress;
        string? poll = configuration["Registry:PollSeconds"];

        await harness.PublishReleaseAsync("app1", SecondRelease, NextRelease("app1", SecondRelease), Ct);
        BrowserResponse before = await browser.NavigateAsync(harness.App("app1").Url("/"), ct: Ct);
        await harness.UpdateRegistryAsync(document => Release(document, "app1", SecondRelease), Ct);
        BrowserResponse after = await browser.NavigateAsync(harness.App("app1").Url("/"), ct: Ct);

        // The content changed...
        Assert.NotEqual(before.Bytes, after.Bytes);

        // ...and nothing was deployed to make it change. A restart would give a new service provider
        // and a new server; a reconfiguration would move the poll interval the poller runs on.
        Assert.Same(services, harness.Services);
        Assert.Same(server, harness.Services.GetRequiredService<IServer>());
        Assert.Equal(address, harness.GatewayAddress);
        Assert.Equal(poll, configuration["Registry:PollSeconds"]);
        Assert.Equal(GatewayHarness.PollSeconds.ToString(CultureInfo.InvariantCulture), poll);
    }

    [Fact]
    public async Task A_switch_leaves_the_other_app_untouched()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser browser = harness.CreateBrowser();

        BrowserResponse neighbourBefore = await browser.NavigateAsync(harness.App("app2").Url("/"), ct: Ct);
        await harness.PublishReleaseAsync("app1", SecondRelease, NextRelease("app1", SecondRelease), Ct);
        await harness.UpdateRegistryAsync(document => Release(document, "app1", SecondRelease), Ct);
        BrowserResponse neighbourAfter = await browser.NavigateAsync(harness.App("app2").Url("/"), ct: Ct);
        BrowserResponse switched = await browser.NavigateAsync(harness.App("app1").Url("/"), ct: Ct);

        // One app's rollout cannot disturb its neighbour: the same release, the same bytes and the
        // same policy, while the app beside it is serving something else.
        Assert.Equal(harness.Options.Release, harness.Registry.Current.ByAppId["app2"].Release);
        Assert.Equal(SecondRelease, harness.Registry.Current.ByAppId["app1"].Release);
        Assert.Equal(FixtureFiles.Spa("app2")["index.html"], neighbourAfter.Bytes);
        Assert.Equal(neighbourBefore.Bytes, neighbourAfter.Bytes);
        Assert.Equal(neighbourBefore.Header("Content-Security-Policy"), neighbourAfter.Header("Content-Security-Policy"));
        Assert.NotEqual(neighbourAfter.Bytes, switched.Bytes);
    }

    [Fact]
    public async Task The_live_registry_revision_is_observable_after_a_switch()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser browser = harness.CreateBrowser();
        await harness.PublishReleaseAsync("app1", SecondRelease, NextRelease("app1", SecondRelease), Ct);

        long before = await RevisionAsync(browser, Ct);
        await harness.UpdateRegistryAsync(document => Release(document, "app1", SecondRelease), Ct);
        long after = await RevisionAsync(browser, Ct);
        await harness.UpdateRegistryAsync(document => Release(document, "app1", harness.Options.Release), Ct);
        long restored = await RevisionAsync(browser, Ct);

        // /readyz is what spactl wait and the ALB health check read (contracts/pipeline.md -> Health
        // endpoints), so it is how an operator tells which document is live.
        Assert.Equal(1, before);
        Assert.Equal(2, after);
        Assert.Equal(3, restored);
        Assert.Equal(restored, harness.Registry.Current.Revision);
    }

    /// <summary>The revision <c>/readyz</c> reports, on any host (stage 3 runs before host resolution).</summary>
    private static async Task<long> RevisionAsync(TestBrowser browser, CancellationToken ct)
    {
        BrowserResponse ready = await browser.NavigateAsync("https://anything.example/readyz", ct: ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using JsonDocument body = JsonDocument.Parse(ready.Body);
        return body.RootElement.GetProperty("revision").GetInt64();
    }

    /// <summary>Points one app's entry at a release, which is the whole of an operator's rollout.</summary>
    private static void Release(JsonObject document, string appId, string release) =>
        document["apps"]!.AsArray().Single(app => app!["appId"]!.GetValue<string>() == appId)!
            .AsObject()["release"] = release;

    /// <summary>
    /// A second release of an app: its fixture files, with a line in the shell that says which release
    /// it is. Derived from the first release rather than written out, so the two differ in one place
    /// and the comparison that follows is about the prefix the bytes came from.
    /// </summary>
    private static IReadOnlyDictionary<string, byte[]> NextRelease(string appId, string release)
    {
        Dictionary<string, byte[]> files = new(FixtureFiles.Spa(appId), StringComparer.Ordinal);
        const string doctype = "<!doctype html>";
        string shell = Encoding.UTF8.GetString(files["index.html"]);
        files["index.html"] = Encoding.UTF8.GetBytes(
            shell.Replace(doctype, doctype + "\n<!-- release " + release + " -->", StringComparison.Ordinal));

        // The fixture shell is the thing being edited, so an edit that stopped applying must say so
        // here rather than leave the tests comparing a release with itself.
        Assert.Contains(doctype, shell, StringComparison.Ordinal);
        Assert.NotEqual(FixtureFiles.Spa(appId)["index.html"], files["index.html"]);
        return files;
    }
}
