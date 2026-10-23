using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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

        Assert.Equal(200, response.StatusCode);
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

        Assert.Equal(404, response.StatusCode);
        Assert.Equal(0, gateway.Reached);
    }

    [Fact]
    public async Task Readyz_is_503_before_a_snapshot_and_reports_the_live_revision_after()
    {
        await using HostingHost gateway = await HostingHost.StartAsync(Snapshots.None, Ct);

        RawResponse before = await gateway.GetAsync("nope.example", "/readyz", Ct);
        Assert.Equal(503, before.StatusCode);

        // Bodiless (change request P2-5, decision 3): an answer with no instance names no instance, and
        // spactl wait prints it unattributed.
        Assert.Equal("", before.BodyText);

        gateway.Registry.Swap(Snapshots.With(7, TestApps.Entry("app1")));
        RawResponse first = await gateway.GetAsync("nope.example", "/readyz", Ct);

        Assert.Equal(200, first.StatusCode);
        Assert.Equal("application/json", first.Header("content-type")!.Split(';')[0]);
        Assert.Equal(7, JsonDocument.Parse(first.BodyText).RootElement.GetProperty("revision").GetInt64());

        // Live, not the first one seen: the next swap is reported on the next request.
        gateway.Registry.Swap(Snapshots.With(8, TestApps.Entry("app1")));
        RawResponse second = await gateway.GetAsync("app1.spa-poc.test", "/readyz", Ct);

        Assert.Equal(8, JsonDocument.Parse(second.BodyText).RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(0, gateway.Reached);
    }

    /// <summary>
    /// The 200 body names the instance that answered beside the revision (contracts/pipeline.md ->
    /// Health endpoints; change request P2-5). The id here is the one the host was given through its
    /// services, as the harness gives one (P2-5, decision 2).
    /// </summary>
    [Fact]
    public async Task Readyz_names_the_instance_that_answered_beside_the_revision()
    {
        await using HostingHost gateway = await HostingHost.StartAsync(
            Snapshots.With(4, TestApps.Entry("app1")), Ct, instance: new GatewayInstance("instance-a"));

        RawResponse ready = await gateway.GetAsync("nope.example", "/readyz", Ct);

        Assert.Equal(200, ready.StatusCode);
        JsonElement body = JsonDocument.Parse(ready.BodyText).RootElement;
        Assert.Equal(4, body.GetProperty("revision").GetInt64());
        Assert.Equal("instance-a", body.GetProperty("instance").GetString());
    }

    /// <summary>
    /// Off ECS the id is generated per host, not per process: two hosts in one test process are two
    /// instances (GLOSSARY.md, Instance), and each keeps its id from one answer to the next.
    /// </summary>
    [Fact]
    public async Task Without_the_metadata_endpoint_each_host_names_itself_with_an_id_of_its_own()
    {
        await using HostingHost a = await HostingHost.StartAsync(Snapshots.With(1, TestApps.Entry("app1")), Ct);
        await using HostingHost b = await HostingHost.StartAsync(Snapshots.With(1, TestApps.Entry("app1")), Ct);

        string firstOfA = await InstanceOf(a);
        string againOfA = await InstanceOf(a);
        string firstOfB = await InstanceOf(b);

        Assert.False(string.IsNullOrWhiteSpace(firstOfA));
        Assert.Equal(firstOfA, againOfA);
        Assert.NotEqual(firstOfA, firstOfB);
        Assert.Contains(a.Log.Lines, line => line.Level == LogLevel.Information && line.Message.Contains(firstOfA, StringComparison.Ordinal));
    }

    /// <summary>
    /// In a cell the id is the ECS task id: the last segment of <c>TaskARN</c> in the task metadata
    /// endpoint v4's <c>/task</c> answer, never the ARN, which carries the account id and the Region
    /// (change request P2-5, decision 1). The answer below is the shape the ECS documentation shows.
    /// </summary>
    [Fact]
    public async Task On_ecs_the_instance_is_the_task_id_from_the_metadata_endpoint()
    {
        MetadataStub metadata = new(HttpStatusCode.OK, """
            {
              "Cluster": "arn:aws:ecs:us-west-2:111122223333:cluster/default",
              "TaskARN": "arn:aws:ecs:us-west-2:111122223333:task/default/158d1c8083dd49d6b527399fd6414f5c",
              "Family": "gateway",
              "Revision": "7",
              "DesiredStatus": "RUNNING",
              "KnownStatus": "RUNNING"
            }
            """);
        CapturedLog log = new();

        string id = await GatewayInstance.ResolveAsync(
            "http://169.254.170.2/v4/0c2a4d1c-1111-2222-3333-444455556666", metadata, TimeSpan.FromSeconds(2), log.CreateLogger<HealthTests>(), Ct);

        Assert.Equal("158d1c8083dd49d6b527399fd6414f5c", id);
        Assert.Equal(new Uri("http://169.254.170.2/v4/0c2a4d1c-1111-2222-3333-444455556666/task"), Assert.Single(metadata.Requests));
        Assert.DoesNotContain(log.Lines, line => line.Text.Contains("111122223333", StringComparison.Ordinal));
    }

    /// <summary>
    /// A metadata endpoint that is there but fails never fails the start: the instance falls back to a
    /// generated id, with one Information line (change request P2-5, decision 2).
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, "{\"Cluster\": \"default\"}")]
    [InlineData(HttpStatusCode.OK, "{\"TaskARN\": \"arn:aws:ecs:us-west-2:111122223333:task/default/NOT-HEX\"}")]
    public async Task A_failing_metadata_endpoint_falls_back_to_a_generated_id(HttpStatusCode status, string body)
    {
        MetadataStub metadata = new(status, body);
        CapturedLog log = new();

        string id = await GatewayInstance.ResolveAsync(
            "http://169.254.170.2/v4/x", metadata, TimeSpan.FromSeconds(2), log.CreateLogger<HealthTests>(), Ct);

        Assert.Single(metadata.Requests);
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Contains(log.Lines, line => line.Level == LogLevel.Information && line.Message.Contains(id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_metadata_endpoint_that_does_not_answer_in_time_falls_back_to_a_generated_id()
    {
        MetadataStub metadata = new(HttpStatusCode.OK, "{}") { Hang = true };

        string id = await GatewayInstance.ResolveAsync(
            "http://169.254.170.2/v4/x", metadata, TimeSpan.FromMilliseconds(50), new CapturedLog().CreateLogger<HealthTests>(), Ct);

        Assert.Matches("^[0-9a-f]{32}$", id);
    }

    /// <summary>
    /// A variable that is set but is not an absolute http URI falls back too, rather than faulting the
    /// id, which every ready <c>/readyz</c> awaits and would then fail on for the host's lifetime
    /// (change request P2-5, decision 2: never failing the start).
    /// </summary>
    [Theory]
    [InlineData("not a uri")]
    [InlineData("/v4/relative")]
    [InlineData("file:///etc/passwd")]
    public async Task A_metadata_variable_that_is_not_an_http_uri_falls_back_to_a_generated_id(string variable)
    {
        MetadataStub metadata = new(HttpStatusCode.OK, "{}");
        CapturedLog log = new();

        string id = await GatewayInstance.ResolveAsync(
            variable, metadata, TimeSpan.FromSeconds(2), log.CreateLogger<HealthTests>(), Ct);

        Assert.Empty(metadata.Requests);
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Contains(log.Lines, line => line.Level == LogLevel.Information && line.Message.Contains(id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_no_metadata_variable_nothing_is_read_and_the_id_is_generated()
    {
        MetadataStub metadata = new(HttpStatusCode.OK, "{}");
        CapturedLog log = new();

        string first = await GatewayInstance.ResolveAsync(null, metadata, TimeSpan.FromSeconds(2), log.CreateLogger<HealthTests>(), Ct);
        string second = await GatewayInstance.ResolveAsync("", metadata, TimeSpan.FromSeconds(2), log.CreateLogger<HealthTests>(), Ct);

        Assert.Empty(metadata.Requests);
        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.NotEqual(first, second);
        Assert.Equal(2, log.Lines.Count(line => line.Level == LogLevel.Information));
    }

    private static async Task<string> InstanceOf(HostingHost host)
    {
        RawResponse ready = await host.GetAsync("nope.example", "/readyz", Ct);
        Assert.Equal(200, ready.StatusCode);
        return JsonDocument.Parse(ready.BodyText).RootElement.GetProperty("instance").GetString()!;
    }

    /// <summary>The task metadata endpoint v4, answering <c>/task</c> with a fixed status and body.</summary>
    private sealed class MetadataStub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        public bool Hang { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
