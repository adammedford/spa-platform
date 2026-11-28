using System.Net;
using System.Text.Json.Nodes;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Gateway.TestHarness;
using Microsoft.AspNetCore.WebUtilities;
using Platform.Registry;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// The reference onboarding procedure, run end to end through <c>spactl</c>'s own entry point, on
/// <c>standard</c> against the test identity provider, with no apps root (#1516, part of #1493). The
/// steps run in the onboarding note's order: <c>spactl app validate</c>; the client at the provider; the
/// secret in the store at a pinned version; the outputs document's <c>clients</c> entry; the first
/// release; <c>spactl app add --release</c>; <c>spactl wait</c>. Removal runs the inverse, registry
/// first. The provider, the store and the outputs document are written here by hand, as an adopter's
/// own tooling writes them, so the order is one an adopter can copy.
///
/// Every host reads its client secrets through the real <c>ParameterStoreClientSecretSource</c>, each
/// with its own cache, over one Parameter Store stub for the cell
/// (<see cref="GatewayHarnessOptions.SecretsThroughParameterStore"/>): the early-deletion control rests
/// on that cache, which the shared fake does not have. The hosts have no background poller, so each
/// test polls a host itself after a write and asserts what the poll did before it reads a
/// <c>wait</c>'s verdict: a wait that times out proves a hold only when the host was polled and refused.
/// </summary>
public sealed class AdopterOnboardingTests
{
    /// <summary>A release of app 1 other than the one it starts at: the unrelated change.</summary>
    private const string NextRelease = "2026-10-11.1";

    /// <summary>The upstream sentence of the bounds refusal (#1512), which the run asserts.</summary>
    private const string UpstreamRefusal =
        "The entry is outside the cell's bounds: adding an upstream is widening the bounds, an operator's change, not a registry write.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One host in cell mode (a key ring, so a later host can start), reading secrets through the real source.</summary>
    private static GatewayHarnessOptions Cell => new()
    {
        KeyRing = KeyRingSharing.OneParameterStore,
        SecretsThroughParameterStore = true,
    };

    /// <summary>
    /// F-44: onboarding with no apps root. <c>app validate</c> refuses app 3 while its upstream lies
    /// outside the bounds, before any client exists; the corrected definition passes; the client, the
    /// secret and the outputs entry are written by hand; <c>release publish</c>, <c>app add --release</c>
    /// and <c>wait</c> exit 0, and app 3 logs in with its own client with no restart.
    /// </summary>
    [Fact]
    public async Task Onboarding_with_no_apps_root_validates_first_then_publishes_adds_and_waits_and_app_3_logs_in_with_no_restart()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(Cell, Ct);
        HarnessSpactl spactl = new(harness);
        using TestBrowser one = harness.CreateBrowser();
        using TestBrowser two = harness.CreateBrowser();
        using TestBrowser three = harness.CreateBrowser();
        await harness.SignInAsync(one, "app1", Ct);
        await harness.SignInAsync(two, "app2", Ct);
        Assert.Equal(HttpStatusCode.OK, (await one.GetAsync("/api/orders/whoami", ct: Ct)).StatusCode);

        // 1. The draft names an upstream outside Policy:AllowedUpstreams: refused offline, naming widening,
        //    before anyone has registered a client.
        JsonObject draft = harness.DefinitionFor("app3");
        draft["apis"]!.AsArray().OfType<JsonObject>().Single(api => api["name"]!.GetValue<string>() == "reports")["upstream"] =
            "https://reports.outside-the-bounds.test";
        SpactlRun refused = await spactl.RunAsync(Ct, "app", "validate", spactl.WriteFile("app3-draft.json", draft), "--config", spactl.ConfigPath);

        // Mutation that turns it red: the draft's upstream left inside the bounds (the line above removed).
        Assert.Equal(1, refused.Exit);
        Assert.Contains(UpstreamRefusal, refused.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("adding an issuer", refused.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(GatewayHarness.ClientIdFor("app3"), harness.Idp.Clients.Keys);

        // 2. The corrected definition passes.
        string definition = spactl.WriteFile("app3.json", harness.DefinitionFor("app3"));
        SpactlRun valid = await spactl.RunAsync(Ct, "app", "validate", definition, "--config", spactl.ConfigPath);
        Assert.Equal(0, valid.Exit);

        // The note's steps 2 to 7 but 5: the client, the secret at version 1, the outputs entry, the
        // release, app add and wait.
        HarnessApp app3 = await OnboardApp3Async(harness, spactl, definition);
        BrowserResponse landed = await harness.SignInAsync(three, app3, Ct);

        // Its own shell, after a login with its own client to its own callback.
        Assert.Equal(FixtureFiles.Spa("app3")["index.html"], landed.Bytes);
        Uri authorize = Assert.Single(landed.RedirectedFrom, hop => hop.AbsoluteUri.StartsWith(harness.Idp.AuthorizeEndpoint, StringComparison.Ordinal));
        Assert.Equal(app3.ClientId, QueryHelpers.ParseQuery(authorize.Query)["client_id"].ToString());
        Assert.Equal(app3.Url("/signin-oidc").ToString(), QueryHelpers.ParseQuery(authorize.Query)["redirect_uri"].ToString());

        // No restart: the one host took app 3 by a registry swap (asserted in OnboardApp3Async), and
        // apps 1 and 2 kept their sessions throughout.
        Assert.Equal(HttpStatusCode.OK, (await one.GetAsync("/api/orders/whoami", ct: Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await two.GetAsync("/api/reports/whoami", ct: Ct)).StatusCode);
        Assert.Equal(3, harness.Idp.Authorizations.Count);
    }

    /// <summary>
    /// F-45, first case: with app 3's outputs entry written and no parameter, <c>app add</c> is followed
    /// by a <c>wait</c> that times out with the live revision unchanged, and an unrelated release
    /// activation for app 1 is held as well; once the parameter is written, the next poll applies the
    /// revision and <c>wait</c> exits 0.
    /// </summary>
    [Fact]
    public async Task An_unreadable_secret_holds_every_later_registry_change_until_its_parameter_is_written()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(Cell, Ct);
        HarnessSpactl spactl = new(harness);
        HarnessApp app3 = await StallOnApp3Async(harness, spactl);

        // An unrelated change, app 1's next release, is held behind it.
        await ActivateApp1NextReleaseAsync(spactl);
        Assert.Equal(RefreshOutcome.SecretsUnreadable, await harness.ReloadRegistryDocumentAsync(Ct));
        Assert.Equal(1, harness.Registry.Current.Revision);
        SpactlRun held = await spactl.RunAsync(Ct, "wait", "--url", HarnessSpactl.Url, "--revision", "3", "--timeout", "1");
        Assert.Equal(1, held.Exit);
        Assert.Contains("the last answer was revision 1", held.Error, StringComparison.Ordinal);

        // Recovery one: the parameter is written, and the next poll applies the held revision.
        await WriteSecretAsync(harness, app3);
        Task<SpactlRun> wait = spactl.RunAsync(Ct, "wait", "--url", HarnessSpactl.Url, "--revision", "3", "--timeout", "60");
        Assert.Equal(RefreshOutcome.Swapped, await harness.RefreshRegistryAsync(Ct));
        SpactlRun ready = await wait.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(0, ready.Exit);
        Assert.Equal(3, harness.Registry.Current.Revision);
        Assert.True(harness.Registry.Current.ByAppId.ContainsKey("app3"));
        Assert.Equal(NextRelease, harness.Registry.Current.ByAppId["app1"].Release);
        using TestBrowser browser = harness.CreateBrowser();
        Assert.Equal(FixtureFiles.Spa("app3")["index.html"], (await harness.SignInAsync(browser, app3, Ct)).Bytes);
    }

    /// <summary>F-45, second case: the stall freed by <c>app remove</c>, after which <c>wait</c> exits 0.</summary>
    [Fact]
    public async Task An_unreadable_secret_is_freed_by_app_remove()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(Cell, Ct);
        HarnessSpactl spactl = new(harness);
        await StallOnApp3Async(harness, spactl);

        SpactlRun removed = await spactl.RunAsync(Ct, "app", "remove", "app3", "--outputs", spactl.OutputsPath, "--config", spactl.ConfigPath);
        Assert.Equal(0, removed.Exit);
        Assert.Equal(RefreshOutcome.Swapped, await harness.ReloadRegistryDocumentAsync(Ct));
        SpactlRun ready = await spactl.RunAsync(Ct, "wait", "--url", HarnessSpactl.Url, "--revision", "3", "--timeout", "30");

        Assert.Equal(0, ready.Exit);
        Assert.Equal(3, harness.Registry.Current.Revision);
        Assert.False(harness.Registry.Current.ByAppId.ContainsKey("app3"));
        Assert.Equal<string>(["app1", "app2"], [.. harness.Registry.Current.ByAppId.Keys.Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// F-46: removal runs registry first. <c>app remove</c> and <c>wait</c> exit 0, app 3's host answers
    /// 404 and its session is gone, and only then are the parameter, the outputs entry and the client at
    /// the provider deleted, after which an unrelated change still converges. It runs the procedure; it
    /// cannot fail on removal's order, since a run that deletes the parameter and the client before
    /// <c>app remove</c> passes as well, the running host having the secret cached. The control below
    /// carries why the order matters: it goes red when the parameter is kept.
    /// </summary>
    [Fact]
    public async Task Removal_runs_registry_first_and_leaves_an_unrelated_change_converging()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(Cell, Ct);
        HarnessSpactl spactl = new(harness);
        HarnessApp app3 = await OnboardApp3Async(harness, spactl, spactl.WriteFile("app3.json", harness.DefinitionFor("app3")));
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Ct);
        string app1Session = harness.SessionStore!.Created[^1];
        await harness.SignInAsync(browser, app3, Ct);
        string app3Session = harness.SessionStore.Created[^1];
        Assert.Equal("app3", (await harness.SessionStore.GetAsync(app3Session, Ct))?.AppId);

        // The registry first.
        SpactlRun removed = await spactl.RunAsync(Ct, "app", "remove", "app3", "--outputs", spactl.OutputsPath, "--config", spactl.ConfigPath);
        Assert.Equal(0, removed.Exit);
        Assert.Equal(RefreshOutcome.Swapped, await harness.ReloadRegistryDocumentAsync(Ct));
        SpactlRun converged = await spactl.RunAsync(Ct, "wait", "--url", HarnessSpactl.Url, "--revision", "3", "--timeout", "30");
        Assert.Equal(0, converged.Exit);

        BrowserResponse gone = await browser.NavigateAsync(app3.Url("/"), new RequestOptions { Redirect = RedirectMode.Manual }, Ct);
        Assert.Equal((HttpStatusCode)Contract.Error("not_found").Status, gone.StatusCode);
        Assert.Null(await harness.SessionStore.GetAsync(app3Session, Ct));
        Assert.NotNull(await harness.SessionStore.GetAsync(app1Session, Ct));

        // Only then what the registry named: the parameter, the outputs entry and the client.
        (string parameter, _) = GatewayHarness.SecretParameter(app3.SecretRef);
        await harness.SecretParameters!.DeleteParameterAsync(new DeleteParameterRequest { Name = parameter }, Ct);
        spactl.RemoveClient("app3");
        Assert.True(harness.Idp.RemoveClient(app3.ClientId));

        // An unrelated change still converges: app 1's next release goes live, and its session holds.
        await ActivateApp1NextReleaseAsync(spactl);
        Assert.Equal(RefreshOutcome.Swapped, await harness.ReloadRegistryDocumentAsync(Ct));
        SpactlRun next = await spactl.RunAsync(Ct, "wait", "--url", HarnessSpactl.Url, "--revision", "4", "--timeout", "30");

        Assert.Equal(0, next.Exit);
        Assert.Equal(NextRelease, harness.Registry.Current.ByAppId["app1"].Release);
        BrowserResponse app1 = await browser.NavigateAsync(harness.App("app1").Url("/"), ct: Ct);
        Assert.Equal(HttpStatusCode.OK, app1.StatusCode);
        Assert.Empty(app1.RedirectedFrom);
    }

    /// <summary>
    /// F-46's control: with the parameter deleted while app 3 is live, the running host keeps serving
    /// app 3; a host started afterwards leaves app 3 out at its cold start and answers 404 on its host,
    /// then holds the next unrelated revision, which <c>wait --instances</c> names; <c>app remove</c>
    /// frees it.
    /// </summary>
    [Fact]
    public async Task A_parameter_deleted_while_the_app_is_live_breaks_only_a_host_started_later()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(Cell, Ct);
        HarnessSpactl spactl = new(harness);
        HarnessApp app3 = await OnboardApp3Async(harness, spactl, spactl.WriteFile("app3.json", harness.DefinitionFor("app3")));
        GatewayHost a = harness.Hosts[0];

        // Deleted early, while app 3 is live.
        (string parameter, _) = GatewayHarness.SecretParameter(app3.SecretRef);
        await harness.SecretParameters!.DeleteParameterAsync(new DeleteParameterRequest { Name = parameter }, Ct);

        // The running host still serves app 3: a new login's code exchange uses the secret it cached.
        using (TestBrowser onA = harness.CreateBrowser(() => a))
        {
            Assert.Equal(FixtureFiles.Spa("app3")["index.html"], (await harness.SignInAsync(onA, app3, Ct)).Bytes);
        }

        // A host started afterwards cannot read the secret: app 3 is left out at its cold start.
        GatewayHost b = await harness.StartHostAsync(Ct);
        Assert.Equal(2, b.Registry.Current.Revision);
        Assert.False(b.Registry.Current.ByAppId.ContainsKey("app3"));
        using (TestBrowser onB = harness.CreateBrowser(() => b))
        {
            BrowserResponse unknown = await onB.NavigateAsync(app3.Url("/"), new RequestOptions { Redirect = RedirectMode.Manual }, Ct);
            Assert.Equal((HttpStatusCode)Contract.Error("not_found").Status, unknown.StatusCode);
        }

        // It holds the next unrelated revision, and wait --instances names it.
        await ActivateApp1NextReleaseAsync(spactl);
        Assert.Equal(RefreshOutcome.Swapped, await harness.ReloadRegistryDocumentAsync(Ct));
        Assert.Equal(RefreshOutcome.SecretsUnreadable, await b.RefreshRegistryAsync(Ct));
        Assert.Equal(2, b.Registry.Current.Revision);
        SpactlRun held = await spactl.RunAsync(
            Ct, "wait", "--url", HarnessSpactl.Url, "--revision", "3", "--instances", "host-a,host-b", "--timeout", "3");

        // Mutation that turns it red: host-b polled into a swap (the parameter left in place).
        Assert.Equal(1, held.Exit);
        Assert.Contains("instance host-b last answered revision 2", held.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("instance host-a", held.Error, StringComparison.Ordinal);

        // app remove frees it.
        SpactlRun removed = await spactl.RunAsync(Ct, "app", "remove", "app3", "--outputs", spactl.OutputsPath, "--config", spactl.ConfigPath);
        Assert.Equal(0, removed.Exit);
        Assert.Equal(RefreshOutcome.Swapped, await harness.ReloadRegistryDocumentAsync(Ct));
        Assert.Equal(RefreshOutcome.Swapped, await b.RefreshRegistryAsync(Ct));
        SpactlRun freed = await spactl.RunAsync(
            Ct, "wait", "--url", HarnessSpactl.Url, "--revision", "4", "--instances", "host-a,host-b", "--timeout", "30");

        Assert.Equal(0, freed.Exit);
        Assert.Contains("instance host-a at revision 4, instance host-b at revision 4", freed.Out, StringComparison.Ordinal);
        Assert.Equal(NextRelease, b.Registry.Current.ByAppId["app1"].Release);
    }

    /// <summary>
    /// Steps 2 to 7 of the onboarding note for app 3 but step 5, the release prefix grant, which is IAM
    /// and not hermetic, with its definition already validated: the client at the provider, the secret
    /// at version 1, the outputs entry, the first release, <c>app add --release</c> and <c>wait</c>,
    /// each asserted. Answers the app as the provider knows it.
    /// </summary>
    private static async Task<HarnessApp> OnboardApp3Async(GatewayHarness harness, HarnessSpactl spactl, string definition)
    {
        HarnessApp app3 = RegisterApp3(harness);
        await WriteSecretAsync(harness, app3);
        WriteOutputsEntry(spactl, app3);
        await AddApp3Async(harness, spactl, definition);
        Assert.Equal(RefreshOutcome.Swapped, await harness.ReloadRegistryDocumentAsync(Ct));
        SpactlRun ready = await spactl.RunAsync(Ct, "wait", "--url", HarnessSpactl.Url, "--revision", "2", "--timeout", "30");
        Assert.Equal(0, ready.Exit);
        return app3;
    }

    /// <summary>
    /// App 3 onboarded with no parameter in the store: the client and the outputs entry written, then
    /// <c>app add</c>. The poll refuses the revision and <c>wait</c> times out at the live one.
    /// </summary>
    private static async Task<HarnessApp> StallOnApp3Async(GatewayHarness harness, HarnessSpactl spactl)
    {
        HarnessApp app3 = RegisterApp3(harness);
        WriteOutputsEntry(spactl, app3);
        await AddApp3Async(harness, spactl, spactl.WriteFile("app3.json", harness.DefinitionFor("app3")));

        // Mutation that turns it red: the parameter written before app add, by inserting
        // `await WriteSecretAsync(harness, app3);` ahead of AddApp3Async above; both F-45 tests go red.
        Assert.Equal(RefreshOutcome.SecretsUnreadable, await harness.ReloadRegistryDocumentAsync(Ct));
        Assert.Equal(1, harness.Registry.Current.Revision);
        SpactlRun stalled = await spactl.RunAsync(Ct, "wait", "--url", HarnessSpactl.Url, "--revision", "2", "--timeout", "1");
        Assert.Equal(1, stalled.Exit);
        Assert.Contains("the last answer was revision 1", stalled.Error, StringComparison.Ordinal);
        return app3;
    }

    /// <summary>
    /// The client at the provider, with the <c>standard</c> callback and post-logout return, as an
    /// adopter's tooling registers it. Its secret is random per run, so a value seen in a log line is
    /// this run's (N-16).
    /// </summary>
    private static HarnessApp RegisterApp3(GatewayHarness harness)
    {
        string host = harness.DefinitionFor("app3")["host"]!.GetValue<string>();
        HarnessApp app3 = new(
            "app3",
            host,
            new Uri("https://" + host + "/"),
            GatewayHarness.ClientIdFor("app3"),
            "secret-app3-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
            "ssm:/spa-platform/harness/apps/app3/client-secret:1");
        harness.Idp.AddClient(app3.ClientId, app3.ClientSecret, app3.Url("/signin-oidc").ToString(), app3.Url("/signout-callback-oidc").ToString());
        return app3;
    }

    /// <summary>The outputs document's <c>clients.app3</c>, naming the parameter at the version its reference pins.</summary>
    private static void WriteOutputsEntry(HarnessSpactl spactl, HarnessApp app)
    {
        (string parameter, long version) = GatewayHarness.SecretParameter(app.SecretRef);
        spactl.WriteClient(app.AppId, app.ClientId, parameter, version);
    }

    /// <summary>The provider's client secret alone, into the cell's store; the store must number it as the reference pins.</summary>
    private static async Task WriteSecretAsync(GatewayHarness harness, HarnessApp app)
    {
        (string parameter, long version) = GatewayHarness.SecretParameter(app.SecretRef);
        PutParameterResponse put = await harness.SecretParameters!.PutParameterAsync(
            new PutParameterRequest { Name = parameter, Value = app.ClientSecret, Type = ParameterType.SecureString, Overwrite = true }, Ct);
        Assert.Equal(version, put.Version);
    }

    /// <summary>The team's first release, then <c>app add --release</c>.</summary>
    private static async Task AddApp3Async(GatewayHarness harness, HarnessSpactl spactl, string definition)
    {
        string release = harness.Options.Release;
        SpactlRun published = await spactl.RunAsync(Ct, "release", "publish", "app3", FixtureDirectory("app3"), release, "--outputs", spactl.OutputsPath);
        Assert.Equal(0, published.Exit);
        SpactlRun added = await spactl.RunAsync(
            Ct, "app", "add", definition, "--release", release, "--outputs", spactl.OutputsPath, "--config", spactl.ConfigPath);
        Assert.Equal(0, added.Exit);
    }

    /// <summary>App 1's next release published and activated: a registry change unrelated to app 3.</summary>
    private static async Task ActivateApp1NextReleaseAsync(HarnessSpactl spactl)
    {
        SpactlRun published = await spactl.RunAsync(
            Ct, "release", "publish", "app1", FixtureDirectory("app1"), NextRelease, "--outputs", spactl.OutputsPath);
        Assert.Equal(0, published.Exit);
        SpactlRun activated = await spactl.RunAsync(
            Ct, "release", "activate", "app1", NextRelease, "--outputs", spactl.OutputsPath, "--config", spactl.ConfigPath);
        Assert.Equal(0, activated.Exit);
    }

    private static string FixtureDirectory(string appId) => Path.Combine(FixtureFiles.RepositoryRoot, "fixtures", "spa", appId);
}
