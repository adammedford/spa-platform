using System.Text.Json.Nodes;

namespace Spactl.Tests;

/// <summary>
/// <c>spactl wait</c>, which reads <c>/readyz</c> (contracts/pipeline.md, Health endpoints), and
/// <c>spactl registry rollback</c>, which republishes an earlier version of the registry object under a
/// new revision (docs/plan/05-infrastructure-and-cost.md, "Deployment and rollback"). Rollback runs here
/// against the versioned in-memory store; task 6.9 runs it on the deployed bucket.
/// </summary>
public sealed class WaitAndRollbackTests : IDisposable
{
    private const string Gateway = "https://app1.spa-poc.test:8443";

    private readonly Harness spactl = new();

    public void Dispose() => spactl.Dispose();

    private async Task Release(string release)
    {
        await spactl.Run("release", "publish", "app1", Harness.Example("spa", "app1"), release);
    }

    private async Task<JsonObject> Live() => await spactl.LiveRegistry();

    private static long RevisionOf(JsonObject document) => document["revision"]!.GetValue<long>();

    private static string ReleaseOf(JsonObject document) => document["apps"]![0]!["release"]!.GetValue<string>();

    [Fact]
    public async Task Wait_returns_once_readyz_reports_the_revision()
    {
        await Release("r1");
        await spactl.Run("app", "add", Harness.Example("apps", "app1.json"), "--release", "r1");
        spactl.Readyz.Lag = 3;

        Assert.Equal(0, await spactl.RunRaw("wait", "--url", Gateway, "--revision", "1"));

        Assert.Equal(4, spactl.Readyz.Requests.Count);
        Assert.All(spactl.Readyz.Requests, uri => Assert.Equal(new Uri(Gateway + "/readyz"), uri));
    }

    [Fact]
    public async Task Wait_accepts_a_later_revision_than_the_one_named()
    {
        await Release("r1");
        await spactl.Run("app", "add", Harness.Example("apps", "app1.json"), "--release", "r1");
        await Release("r2");
        await spactl.Run("release", "activate", "app1", "r2");

        Assert.Equal(0, await spactl.RunRaw("wait", "--url", Gateway, "--revision", "1"));
    }

    [Fact]
    public async Task Wait_fails_when_the_revision_is_not_reported_before_the_timeout()
    {
        await Release("r1");
        await spactl.Run("app", "add", Harness.Example("apps", "app1.json"), "--release", "r1");

        Assert.Equal(1, await spactl.RunRaw("wait", "--url", Gateway, "--revision", "2", "--timeout", "0"));

        Assert.Contains("revision 1", spactl.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wait_fails_on_a_gateway_that_never_became_ready()
    {
        Assert.Equal(1, await spactl.RunRaw("wait", "--url", Gateway, "--revision", "1", "--timeout", "0"));
        Assert.Contains("HTTP 503", spactl.Error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--url", "not a url", "--revision", "1")]
    [InlineData("--url", Gateway, "--revision", "0")]
    [InlineData("--url", Gateway, "--revision", "one")]
    [InlineData("--revision", "1", "--url", "ftp://gateway")]
    public async Task Wait_with_a_bad_url_or_revision_is_a_usage_error(string a, string b, string c, string d)
    {
        Assert.Equal(2, await spactl.RunRaw("wait", a, b, c, d));
        Assert.Empty(spactl.Readyz.Requests);
    }

    [Fact]
    public async Task Rollback_republishes_the_previous_version_under_a_new_revision()
    {
        await Release("r1");
        await Release("r2");
        await spactl.Run("app", "add", Harness.Example("apps", "app1.json"), "--release", "r1");
        await spactl.Run("release", "activate", "app1", "r2");

        Assert.Equal(0, await spactl.Run("registry", "rollback"));

        JsonObject live = await Live();
        Assert.Equal(3, RevisionOf(live));
        Assert.Equal("r1", ReleaseOf(live));
        Assert.Equal(3, spactl.Store.VersionCount(Harness.RegistryKey));
    }

    [Fact]
    public async Task Rollback_twice_returns_to_where_the_first_rollback_started()
    {
        // The version before a rollback is the one it rolled back from (Commands.RegistryRollbackAsync).
        await Release("r1");
        await Release("r2");
        await spactl.Run("app", "add", Harness.Example("apps", "app1.json"), "--release", "r1");
        await spactl.Run("release", "activate", "app1", "r2");
        await spactl.Run("registry", "rollback");

        Assert.Equal(0, await spactl.Run("registry", "rollback"));

        JsonObject live = await Live();
        Assert.Equal(4, RevisionOf(live));
        Assert.Equal("r2", ReleaseOf(live));
    }

    [Fact]
    public async Task Rollback_to_a_named_revision_reaches_past_the_previous_version()
    {
        await Release("r1");
        await Release("r2");
        await Release("r3");
        await spactl.Run("app", "add", Harness.Example("apps", "app1.json"), "--release", "r1");
        await spactl.Run("release", "activate", "app1", "r2");
        await spactl.Run("release", "activate", "app1", "r3");

        Assert.Equal(0, await spactl.Run("registry", "rollback", "--to-revision", "1"));

        JsonObject live = await Live();
        Assert.Equal(4, RevisionOf(live));
        Assert.Equal("r1", ReleaseOf(live));
    }

    [Fact]
    public async Task Rollback_to_a_revision_no_version_carries_is_refused()
    {
        await Release("r1");
        await spactl.Run("app", "add", Harness.Example("apps", "app1.json"), "--release", "r1");
        await spactl.Run("release", "activate", "app1", "r1");

        Assert.Equal(1, await spactl.Run("registry", "rollback", "--to-revision", "9"));
        Assert.Equal(1, spactl.Store.VersionCount(Harness.RegistryKey));
    }

    [Fact]
    public async Task Rollback_with_one_version_is_refused()
    {
        await Release("r1");
        await spactl.Run("app", "add", Harness.Example("apps", "app1.json"), "--release", "r1");

        Assert.Equal(1, await spactl.Run("registry", "rollback"));
        Assert.Contains("no earlier version", spactl.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_with_no_registry_is_refused()
    {
        Assert.Equal(1, await spactl.Run("registry", "rollback"));
        Assert.Contains("no live registry", spactl.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_to_content_the_policy_now_refuses_is_refused()
    {
        // The earlier version is validated again, under today's policy: here it named an upstream origin
        // the platform configuration no longer allows.
        await Release("r1");
        await spactl.Run("app", "add", Harness.Example("apps", "app1.json"), "--release", "r1");
        JsonObject withoutApis = Harness.ExampleJson("apps", "app1.json");
        withoutApis.Remove("apis");
        await spactl.Run("app", "add", spactl.WriteFile("app1.json", withoutApis.ToJsonString()));

        JsonObject config = Harness.ExampleJson("platform-config.example.json");
        config["Policy"]!["AllowedUpstreams"] = new JsonArray("http://127.0.0.1:6000");
        string narrowed = spactl.WriteFile("narrowed.json", config.ToJsonString());

        Assert.Equal(1, await spactl.RunRaw("registry", "rollback", "--config", narrowed, "--outputs", spactl.OutputsPath));
        Assert.Contains("upstream-not-allowed", spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, RevisionOf(await Live()));
    }
}
