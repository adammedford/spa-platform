using System.Text.Json;
using Platform.Testing;

namespace Platform.Hosting.Tests;

/// <summary>
/// <c>UsePlatformHealth</c>: contracts/pipeline.md, Health endpoints. Both paths answer on
/// any host, because health runs before host resolution so the ALB health check and
/// <c>spactl wait</c> need no registry entry.
/// </summary>
public sealed class HealthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("nope.example")]
    [InlineData("app1.spa-poc.test")]
    [InlineData("10.0.1.17:8080")]
    public async Task Healthz_answers_200_on_any_host(string host)
    {
        await using HostingHost gateway = await HostingHost.StartAsync(Snapshots.With(1, TestApps.Entry("app1")), Ct);

        RawResponse response = await gateway.GetAsync(host, "/healthz", Ct);

        Assert.Equal(200, response.Status);
        Assert.Equal(0, gateway.Reached);
    }

    /// <summary>
    /// The health paths match ordinally (src/Platform.Hosting/Health.cs): a path differing only in case
    /// is an app path, so it falls through to host resolution, which refuses an unknown host. This fails
    /// if the stage goes back to <c>PathString</c>'s own equality, which ignores case.
    /// </summary>
    [Theory]
    [InlineData("/HEALTHZ")]
    [InlineData("/Readyz")]
    public async Task A_health_path_in_another_case_is_not_a_health_path(string path)
    {
        await using HostingHost gateway = await HostingHost.StartAsync(Snapshots.With(1, TestApps.Entry("app1")), Ct);

        RawResponse response = await gateway.GetAsync("nope.example", path, Ct);

        Assert.Equal(404, response.Status);
        Assert.Equal(0, gateway.Reached);
    }

    [Fact]
    public async Task Readyz_is_503_before_a_snapshot_and_reports_the_live_revision_after()
    {
        await using HostingHost gateway = await HostingHost.StartAsync(Snapshots.None, Ct);

        RawResponse before = await gateway.GetAsync("nope.example", "/readyz", Ct);
        Assert.Equal(503, before.Status);

        gateway.Registry.Swap(Snapshots.With(7, TestApps.Entry("app1")));
        RawResponse first = await gateway.GetAsync("nope.example", "/readyz", Ct);

        Assert.Equal(200, first.Status);
        Assert.Equal("application/json", first.Headers["content-type"].Split(';')[0]);
        Assert.Equal(7, JsonDocument.Parse(first.Body).RootElement.GetProperty("revision").GetInt64());

        // Live, not the first one seen: the next swap is reported on the next request.
        gateway.Registry.Swap(Snapshots.With(8, TestApps.Entry("app1")));
        RawResponse second = await gateway.GetAsync("app1.spa-poc.test", "/readyz", Ct);

        Assert.Equal(8, JsonDocument.Parse(second.Body).RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(0, gateway.Reached);
    }
}
