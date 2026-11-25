using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Gateway.E2ETests.Stack;
using Platform.Abstractions;
using Platform.Registry;
using Platform.Testing;
using Spactl;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// F-T5's drive (<see cref="PublishRaceDrive"/>) with no AWS: spactl's own <c>fragment publish</c>, run in
/// this process over an in-memory bucket that answers a refused create as S3 does (412, seen by
/// <see cref="S3Statuses"/>), the activation and restore recorded, and the winner's URL served from
/// whichever release was last activated. Each test names in its body the mutation that turns it red.
/// </summary>
public sealed class PublishRaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AppId = "shell";

    private static readonly LiveFragment Orders = new("orders", "f1", "entry.js", ["orders"]);

    private static readonly HarnessAppUrl Shell = new(new Uri("https://shell.example.test:8443/"));

    /// <summary>The bucket as S3 answers it: a refused create is a 412 on the wire, which the statuses see.</summary>
    private sealed class Bucket(S3Statuses statuses) : IObjectStore
    {
        public InMemoryObjectStore Inner { get; } = new();

        /// <summary>When each claim's create reached the bucket, oldest first.</summary>
        public ConcurrentQueue<DateTimeOffset> Claims { get; } = new();

        public Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct) => Inner.GetAsync(key, ifNoneMatch, ct);

        public async Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct)
        {
            if (key.Contains("/claims/", StringComparison.Ordinal))
            {
                Claims.Enqueue(DateTimeOffset.UtcNow);
            }

            bool created = await Inner.PutAsync(key, body, ifMatch, ct);
            statuses.Saw(key, created ? 200 : 412);
            return created;
        }
    }

    /// <summary>spactl's publish run for real over the bucket, the second <paramref name="lag"/> late; every other command recorded and answered 0.</summary>
    private sealed class Spactl(TimeSpan lag)
    {
        private int publishes;

        public ConcurrentQueue<string[]> Commands { get; } = new();

        public ConcurrentQueue<DateTimeOffset> Starts { get; } = new();

        public string? Active { get; private set; } = Orders.Release;

        public Func<string[], int?>? Answer { get; init; }

        public async Task<int?> RunAsync(IReadOnlyList<string> arguments, IObjectStore store, CancellationToken ct)
        {
            string[] args = [.. arguments];
            Commands.Enqueue(args);
            if (Answer?.Invoke(args) is { } answered)
            {
                return answered;
            }

            if (args is ["fragment", "activate", _, _, string release, ..])
            {
                Active = release;
                return 0;
            }

            if (Interlocked.Increment(ref publishes) % 2 == 0)
            {
                await Task.Delay(lag, ct);
            }

            Starts.Enqueue(DateTimeOffset.UtcNow);
            int outputs = Array.IndexOf(args, "--outputs");
            string[] local = [.. args[..outputs], "--bucket", "cell-bucket"];
            return await SpactlApp.RunAsync(local, new SpactlEnvironment
            {
                Store = _ => store,
                History = _ => new NoHistory(),
                Http = new SocketsHttpHandler(),
                Out = TextWriter.Null,
                Error = TextWriter.Null,
            }, ct);
        }
    }

    private sealed class NoHistory : IRegistryHistory
    {
        public Task<IReadOnlyList<string>> ListVersionsAsync(string key, CancellationToken ct) => throw new InvalidOperationException("no history here");

        public Task<Stream> OpenVersionAsync(string key, string versionId, CancellationToken ct) => throw new InvalidOperationException("no history here");
    }

    private static (PublishRaceSetup Setup, Bucket Bucket, Spactl Spactl) World(TimeSpan lag, Func<string[], int?>? answer = null, bool seeStatuses = true)
    {
        S3Statuses statuses = new();
        Bucket bucket = new(seeStatuses ? statuses : new S3Statuses());
        Spactl spactl = new(lag) { Answer = answer };
        PublishRaceSetup setup = new(spactl.RunAsync, bucket, statuses, Shell,
            async (url, ct) =>
            {
                string file = url.AbsolutePath[$"/fragments/{Orders.FragmentId}/".Length..];
                StoredObject? stored = await bucket.Inner.GetAsync($"{PublishRaceDrive.Namespace(AppId, Orders.FragmentId)}releases/{spactl.Active}/{file}", null, ct);
                if (stored?.Body is not { } body)
                {
                    return (404, []);
                }

                using MemoryStream copy = new();
                await body.CopyToAsync(copy, ct);
                return (200, copy.ToArray());
            },
            "outputs.json", "platform.json", TimeSpan.FromSeconds(1))
        { Delay = (_, _) => Task.CompletedTask };
        return (setup, bucket, spactl);
    }

    [Fact]
    public async Task Two_publishes_held_at_the_claim_race_once_with_different_bytes_and_both_complete_with_the_same()
    {
        // Mutation: handing spactl setup.Store in place of the ClaimHold in PublishRaceDrive.PublishAsync lets the first
        // run claim before the second has started (it starts 300 ms late here), and fails the first assertion.
        (PublishRaceSetup setup, Bucket bucket, Spactl spactl) = World(TimeSpan.FromMilliseconds(300));

        PublishRaceRecord record = await new PublishRaceDrive(setup).RunAsync(AppId, Orders, "20261124120000", Ct);

        Assert.True(bucket.Claims.Min() >= spactl.Starts.Take(2).Max(), "a claim reached the bucket before both publishes had started");
        RowResult result = FragmentsObserver.PublishRaces(record);
        Assert.True(result.Verdict == Verdict.Passed, result.Message);
        PublishRace different = record.Variants.Single(each => each.Variant == "different");
        Assert.Contains(different.Runs, run => run is { Completed: false, RefusedWith: 412 });
        Assert.All(record.Variants.Single(each => each.Variant == "same").Runs, run => Assert.True(run.Completed));
    }

    [Fact]
    public async Task The_winner_is_activated_for_its_url_and_the_earlier_release_is_activated_again_last()
    {
        // Mutation: dropping the restore from PublishRaceDrive.ServeWinnerAsync leaves the winner live and fails the last assertion.
        (PublishRaceSetup setup, _, Spactl spactl) = World(TimeSpan.Zero);

        await new PublishRaceDrive(setup).RunAsync(AppId, Orders, "20261124120000", Ct);

        string[][] commands = [.. spactl.Commands];
        Assert.Equal(4, commands.Count(command => command is ["fragment", "publish", AppId, "orders", _, _, "--entry", "entry.js", "--outputs", "outputs.json"]));
        string[][] activations = [.. commands.Where(command => command is ["fragment", "activate", ..])];
        Assert.Equal(2, activations.Length);
        Assert.StartsWith("race-20261124120000-different-", activations[0][4], StringComparison.Ordinal);
        Assert.Equal(["fragment", "activate", AppId, "orders", Orders.Release, "--config", "platform.json", "--outputs", "outputs.json"], activations[1]);
        Assert.Equal(Orders.Release, spactl.Active);
    }

    [Fact]
    public async Task A_read_that_throws_still_activates_the_earlier_release_and_the_failure_reaches_the_row()
    {
        // Mutation: running the restore only after a work that answered (Restoring.RunAsync's catch dropped) leaves the
        // winner live here and fails the last assertion.
        (PublishRaceSetup setup, _, Spactl spactl) = World(TimeSpan.Zero);
        PublishRaceSetup failing = setup with { Read = (_, _) => throw new HttpRequestException("the tunnel closed") };

        await Assert.ThrowsAsync<HttpRequestException>(() => new PublishRaceDrive(failing).RunAsync(AppId, Orders, "20261124120000", Ct));

        Assert.Equal(Orders.Release, spactl.Active);
    }

    [Fact]
    public async Task A_restore_that_fails_is_the_rows_failure_naming_the_release_to_activate_by_hand()
    {
        // Mutation: ignoring the restore's exit in PublishRaceDrive.ServeWinnerAsync answers a record here and fails this test.
        (PublishRaceSetup setup, _, _) = World(TimeSpan.Zero,
            args => args is ["fragment", "activate", _, _, string release, ..] && release == Orders.Release ? 1 : null);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => new PublishRaceDrive(setup).RunAsync(AppId, Orders, "20261124120000", Ct));

        Assert.Contains("to release f1 exited 1", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_losers_refusal_is_the_status_s3_answered_never_spactls_exit()
    {
        // Mutation: setting RefusedWith to 412 whenever a run exits non-zero (PublishRaceDrive.PublishAsync) passes the
        // record here, where S3's answer was never seen, and fails this test.
        (PublishRaceSetup setup, _, _) = World(TimeSpan.Zero, seeStatuses: false);

        PublishRaceRecord record = await new PublishRaceDrive(setup).RunAsync(AppId, Orders, "20261124120000", Ct);

        Assert.Contains(record.Variants.Single(each => each.Variant == "different").Runs, run => run is { Completed: false, RefusedWith: null });
        Assert.Equal(Verdict.NotProved, FragmentsObserver.PublishRaces(record).Verdict);
    }

    [Fact]
    public async Task A_run_whose_rival_never_reaches_the_barrier_stops_and_is_recorded_as_not_completed()
    {
        // Mutation: removing the bound from ClaimHold's wait (WaitAsync(wait, ct) to the bare task) hangs this test.
        InMemoryObjectStore store = new();
        ClaimHold hold = new(store, "apps/shell/fragments/orders/claims/assets/x.js", 2, TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TimeoutException>(() => hold.PutAsync(hold.ClaimKey, new MemoryStream([1]), null, Ct));

        Assert.True(await hold.PutAsync("apps/shell/fragments/orders/releases/a/entry.js", new MemoryStream([1]), null, Ct));
        Assert.Equal(1, hold.Arrived);
    }

    [Fact]
    public async Task The_sdks_put_goes_through_the_statuses_and_a_412_is_read_against_the_run_that_sent_it()
    {
        // Mutation: dropping `HttpClientFactory = statuses` from PublishRaceSetup's client (here, from this test's client,
        // which mirrors it) sends the PUT past the tap, so RefusalOf answers null and this test fails.
        int sent = 0;
        S3Statuses statuses = new(() => new Answering(request =>
        {
            Interlocked.Increment(ref sent);
            return new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
            {
                Content = new StringContent("<Error><Code>PreconditionFailed</Code><Message>At least one of the pre-conditions you specified did not hold</Message></Error>", Encoding.UTF8, "application/xml"),
            };
        }));
        using AmazonS3Client client = new(new BasicAWSCredentials("AKIDEXAMPLE", "example"), new AmazonS3Config
        {
            ServiceURL = "http://127.0.0.1:9", ForcePathStyle = true, HttpClientFactory = statuses, MaxErrorRetry = 0, AuthenticationRegion = "eu-west-2",
        });
        S3ObjectStore store = new(client, "cell-bucket");
        const string claim = "apps/shell/fragments/orders/claims/assets/x.js";

        bool created = await statuses.WithinAsync("race-b", () => store.PutAsync(claim, new MemoryStream([1, 2, 3]), null, Ct));

        Assert.False(created);
        Assert.True(sent > 0);
        Assert.Equal(412, statuses.RefusalOf(claim, "race-b"));
        Assert.Null(statuses.RefusalOf(claim, "race-a"));
    }

    private sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
