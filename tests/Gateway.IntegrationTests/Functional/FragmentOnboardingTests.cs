using System.Net;
using System.Text.Json.Nodes;
using Gateway.TestHarness;
using Platform.Abstractions;
using Platform.Registry;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// F-47: a fragment added to a live shell is onboarding, by the onboarding note's fragment section
/// (#1517, part of #1493). With the shell live, the fragment's client is registered at the provider, its
/// secret written and <c>clients.{appId}.fragments.{fragmentId}</c> written into the outputs document by
/// hand, as an adopter's own tooling writes them; the team's first release is published; <c>spactl app
/// add --fragment-release</c> and <c>wait</c> exit 0; and with no gateway restart the shell's login chain
/// reaches the new fragment through its own client.
///
/// <c>spactl</c> runs in process over the harness's bucket (<see cref="HarnessSpactl"/>). The secret goes
/// into the shared secret double, <see cref="GatewayHarness.Secrets"/>: adding a fragment needs no
/// per-host secret cache, so this row uses no Parameter Store stub.
/// </summary>
public sealed class FragmentOnboardingTests
{
    /// <summary>The fragment the shell does not carry at the start, and its first release.</summary>
    private const string Added = "reports";

    private const string AddedRelease = "2026-11-27.1";

    private const string ReturnUrl = "/orders/42";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task G9_a_fragment_added_to_a_live_shell_is_onboarded_with_no_restart_and_the_chain_reaches_it()
    {
        // 1. A shell live with one fragment, and a user signed in to it.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(1), Ct);
        HarnessApp shell = harness.App("shell");
        HarnessFragment orders = Assert.Single(shell.Fragments);
        Assert.NotEqual(Added, orders.FragmentId);
        HarnessSpactl spactl = new(harness);

        // The provider keeps a session, so the new fragment's silent hop is told apart from an interactive
        // sign-in at the provider (#997).
        harness.Idp.Faults.ModelProviderSession = true;
        using TestBrowser before = harness.CreateBrowser();
        Assert.Equal(shell.Url(ReturnUrl), (await Fragments.Fragments.LoginAsync(before, shell, ReturnUrl, Ct)).Url);
        long liveRevision = harness.Registry.Current.Revision;

        // 2. The fragment's client at the provider, as the identity provider contract's fragment-client
        //    subsection has it: one callback, /signin-oidc/{fragmentId} on the shell's host, and no logout
        //    return. Its secret goes into the store at a pinned version, under the shell's reference path.
        HarnessFragment reports = new(
            Added,
            GatewayHarness.ClientIdFor("shell", Added),
            "secret-shell-reports-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
            $"ssm:/spa-platform/harness/apps/shell/fragments/{Added}/client-secret:1");

        // Mutation that turns it red: the client registered with the shell's callback, /signin-oidc, so the
        // provider refuses the new fragment's hop for its redirect_uri and the login does not land.
        harness.Idp.AddClient(reports.ClientId, reports.ClientSecret, shell.Url("/signin-oidc/" + Added).ToString(), []);

        // Mutation that turns it red: this line removed, so the gateway cannot read the secret the entry
        // pins and the poll below answers SecretsUnreadable, not Swapped.
        harness.Secrets[reports.SecretRef] = reports.ClientSecret;

        // The outputs document as the adopter's tooling writes it: the shell's client, and under it one
        // entry per fragment, the live one's as it stands and the new one's.
        string outputs = spactl.WriteFile("outputs.json", Outputs(shell, orders, reports));

        // The handoff: the fragment team's draft item accepted into the shell's definition, which the
        // operator validates first.
        string definition = spactl.WriteFile("shell.json", Definition(harness, shell, orders, reports));
        SpactlRun valid = await spactl.RunAsync(Ct, "app", "validate", definition, "--config", spactl.ConfigPath);
        Assert.True(valid.Exit == 0, valid.ToString());

        // The team's first release, published before the registry names it.
        SpactlRun published = await spactl.RunAsync(
            Ct, "fragment", "publish", "shell", Added, FragmentDirectory(Added), AddedRelease, "--entry", "main.js", "--outputs", outputs);
        Assert.True(published.Exit == 0, published.ToString());

        // 3. app add --fragment-release, then wait.
        SpactlRun added = await spactl.RunAsync(
            Ct, "app", "add", definition, "--fragment-release", $"{Added}={AddedRelease}", "--outputs", outputs, "--config", spactl.ConfigPath);
        Assert.True(added.Exit == 0, added.ToString());
        Assert.Contains($"Replaced shell, at release {harness.Options.Release}.", added.Out, StringComparison.Ordinal);
        Assert.Equal(RefreshOutcome.Swapped, await harness.ReloadRegistryDocumentAsync(Ct));
        string revision = (liveRevision + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        SpactlRun ready = await spactl.RunAsync(Ct, "wait", "--url", HarnessSpactl.Url, "--revision", revision, "--timeout", "30");
        Assert.True(ready.Exit == 0, ready.ToString());

        // The entry app add wrote: the new fragment completed from the outputs, at its first release; the
        // live fragment and the shell kept as they were.
        AppDefinition entry = harness.Registry.Current.ByAppId["shell"];
        Assert.Equal(harness.Options.Release, entry.Release);
        Assert.Equal<string>([orders.FragmentId, Added], [.. entry.Fragments.Select(fragment => fragment.FragmentId)]);
        Assert.Equal(harness.Options.Release, entry.Fragments[0].Release);
        Assert.Equal(AddedRelease, entry.Fragments[1].Release);

        // 4. A login now chains the new fragment, through its own client, silently.
        using TestBrowser after = harness.CreateBrowser();
        int authorizationsBefore = harness.Idp.Authorizations.Count;
        BrowserResponse landed = await Fragments.Fragments.LoginAsync(after, shell, ReturnUrl, Ct);
        Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
        Assert.Equal(shell.Url(ReturnUrl), landed.Url);
        Assert.Equal(
            [shell.ClientId, orders.ClientId, reports.ClientId],
            harness.Idp.Authorizations.Skip(authorizationsBefore).Where(a => a.Ok).Select(a => a.ClientId));
        Assert.True(harness.Idp.Authorizations.Single(a => a.ClientId == reports.ClientId).Silent);
        Assert.Equal(
            new Dictionary<string, string> { [orders.FragmentId] = "signedIn", [Added] = "signedIn" },
            Fragments.Fragments.States(await Fragments.Fragments.SessionAsync(after, shell, Ct)));
        Assert.Equal(reports.ClientId, await Fragments.Fragments.ClientIdAtEchoAsync(harness, after, shell, Added, Ct));

        // No restart: the running host took the fragment by the registry swap asserted above, and the
        // session signed in before the add still answers. It holds no set for the new fragment, which reads
        // loginRequired until its user signs in to it.
        Assert.Equal(
            new Dictionary<string, string> { [orders.FragmentId] = "signedIn", [Added] = "loginRequired" },
            Fragments.Fragments.States(await Fragments.Fragments.SessionAsync(before, shell, Ct)));
        Assert.Equal(orders.ClientId, await Fragments.Fragments.ClientIdAtEchoAsync(harness, before, shell, orders.FragmentId, Ct));
    }

    /// <summary>
    /// The shell's definition with the new fragment's item accepted into it, as the operator hands it to
    /// <c>spactl</c>: the entry the harness would build for the shell with both fragments (the cell's
    /// issuer, the harness's upstreams and the echo API), less what <c>app add</c> completes from the
    /// outputs document and the releases.
    /// </summary>
    private static JsonObject Definition(GatewayHarness harness, HarnessApp shell, HarnessFragment live, HarnessFragment added)
    {
        JsonObject definition = harness.EntryFor(shell with { Fragments = [live, added] }, harness.Options.Release);
        Strip(definition);
        foreach (JsonObject fragment in definition["fragments"]!.AsArray().OfType<JsonObject>())
        {
            Strip(fragment);
        }

        return definition;

        static void Strip(JsonObject item)
        {
            item.Remove("release");
            item["oauth"]!.AsObject().Remove("clientId");
            item["oauth"]!.AsObject().Remove("clientSecretRef");
        }
    }

    /// <summary>The outputs document's <c>clients.shell</c>, with <c>fragments</c> naming each fragment's client and parameter.</summary>
    private static JsonObject Outputs(HarnessApp shell, params HarnessFragment[] fragments)
    {
        JsonObject byFragment = [];
        foreach (HarnessFragment fragment in fragments)
        {
            byFragment[fragment.FragmentId] = Client(fragment.ClientId, fragment.SecretRef);
        }

        JsonObject client = Client(shell.ClientId, shell.SecretRef);
        client["fragments"] = byFragment;
        return new JsonObject
        {
            ["region"] = "us-east-1",
            ["bucket"] = HarnessSpactl.Bucket,
            ["clients"] = new JsonObject { [shell.AppId] = client },
        };

        static JsonObject Client(string clientId, string secretRef)
        {
            (string parameter, long version) = GatewayHarness.SecretParameter(secretRef);
            return new JsonObject { ["clientId"] = clientId, ["secretParameter"] = parameter, ["secretVersion"] = version };
        }
    }

    private static string FragmentDirectory(string fragmentId) =>
        Path.Combine(FixtureFiles.RepositoryRoot, "fixtures", "fragments", "shell", fragmentId);
}
