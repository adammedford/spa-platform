using System.Net;
using Gateway.SecurityTests.Negative;
using Gateway.TestHarness;
using Platform.Abstractions;
using Platform.Registry;

namespace Gateway.SecurityTests.Fragments;

/// <summary>
/// <c>/fragments/</c> on the composed gateway (#1007, part of #961 -> "Fragment routes at the proxy"):
/// the namespace is reserved on a host with no fragments, and an id the shell's entry does not list is
/// not found, with nothing read for it (contracts/request-gate.md; contracts/http-contract.md ->
/// Fragments).
/// </summary>
public sealed class FragmentPathTests
{
    private const string NextRelease = "2026-11-25.1";

    /// <summary>
    /// N-39: <c>/fragments/x/a.js</c> on a host with no fragments answers 404 <c>not_found</c> under
    /// <c>path.reserved</c>, whatever the shell's release holds at <c>fragments/x/a.js</c>.
    /// </summary>
    [Fact]
    public async Task G9_fragments_on_a_fragment_less_host_answers_path_reserved()
    {
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        HarnessApp app = harness.App("app1");

        // A release of app 1 that holds an object at fragments/x/a.js, made live: the namespace must be
        // refused for what it is, not because the key is missing.
        Dictionary<string, byte[]> files = new(FixtureFiles.Spa("app1"), StringComparer.Ordinal)
        {
            ["fragments/x/a.js"] = "export const planted = true;\n"u8.ToArray(),
        };
        await harness.PublishReleaseAsync("app1", NextRelease, files, Suite.Ct);
        Assert.Equal(RefreshOutcome.Swapped, await harness.UpdateRegistryAsync(next =>
            next["apps"]!.AsArray().Single(entry => entry!["appId"]!.GetValue<string>() == "app1")!["release"] = NextRelease, Suite.Ct));

        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Suite.Ct);

        // The control: the same release serves its other files, so the refusal is the path's.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/index.html", ct: Suite.Ct)).StatusCode);

        int before = await Observed.SettledCountAsync(harness, Suite.Ct);
        BrowserResponse response = await browser.GetAsync("/fragments/x/a.js", ct: Suite.Ct);

        // Mutation that turns it red: a request gate whose reserved row skips /fragments/ on a host
        // with no fragments lets the asset route serve the planted object, 200.
        Contract.ErrorRow expected = Contract.Error("not_found");
        Assert.Equal((HttpStatusCode)expected.Status, response.StatusCode);
        Assert.Contains($"\"code\":\"{expected.Code}\"", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("planted", response.Body, StringComparison.Ordinal);
        Assert.Equal(RuleIds.PathReserved, await Observed.RuleOfOneRequestAsync(harness, before, Suite.Ct));
        Assert.Equal(app.Url("/fragments/x/a.js"), response.Url);
    }

    /// <summary>
    /// N-40: <c>/fragments/{unknown}/a.js</c> on a shell host answers 404 <c>not_found</c>, with no rule
    /// id and nothing read from any release.
    /// </summary>
    [Fact]
    public async Task G9_an_unknown_fragment_id_answers_not_found()
    {
        ObjectStoreRecorder recorder = new();
        await using GatewayHarness harness = await Suite.StartAsync(recorder.Options(GatewayHarnessOptions.Shell(1)), Suite.Ct);
        HarnessApp shell = harness.App("shell");
        using TestBrowser browser = harness.CreateBrowser();
        Assert.Equal(HttpStatusCode.OK, (await FragmentLogins.LoginAsync(browser, shell)).StatusCode);

        // The control: a known fragment's chunk is read from that fragment's release, so the recorder
        // sees a fragment read when one happens.
        int controlFrom = recorder.RequestReads.Count;
        BrowserResponse known = await browser.GetAsync("/fragments/orders/assets/panel.d2ff9911.js", ct: Suite.Ct);
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Contains(recorder.RequestsSince(controlFrom), key => key.StartsWith("apps/shell/fragments/orders/releases/", StringComparison.Ordinal));

        int before = await Observed.SettledCountAsync(harness, Suite.Ct);
        int readsFrom = recorder.RequestReads.Count;
        BrowserResponse response = await browser.GetAsync("/fragments/no-such-fragment/a.js", ct: Suite.Ct);

        // Mutation that turns it red: host resolution building a fragment key from the unchecked
        // segment reads apps/shell/fragments/no-such-fragment/..., and the last assertion fails; a
        // fallback to the shell's release for an unknown id reads apps/shell/releases/... and serves
        // index.html, 200, failing the first.
        Contract.ErrorRow expected = Contract.Error("not_found");
        Assert.Equal((HttpStatusCode)expected.Status, response.StatusCode);
        Assert.Contains($"\"code\":\"{expected.Code}\"", response.Body, StringComparison.Ordinal);
        Assert.Null(await Observed.RuleOfOneRequestAsync(harness, before, Suite.Ct));
        Assert.Empty(recorder.RequestsSince(readsFrom));
    }
}
