using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Platform.Abstractions;
using Platform.Registry;
using Spactl;

namespace Gateway.E2ETests.Stack;

/// <summary>spactl run in this process over the store the caller gives it, answering its exit; a run that throws past spactl's own handlers answers null.</summary>
public delegate Task<int?> SpactlRun(IReadOnlyList<string> arguments, IObjectStore store, CancellationToken ct);

/// <summary>
/// F-T5's drive (#1005 F-T5; #961's traces comment, F-T2 against the cell's bucket). Two
/// <c>spactl fragment publish</c> runs of one <c>assets/</c> key, in this process, each holding its claim
/// write at a barrier (<see cref="ClaimHold"/>) until both have reached it, then racing S3's
/// <c>If-None-Match: *</c>. A loser's refusal is the status S3 answered its claim with, read off the
/// HTTP response (<see cref="S3Statuses"/>), never inferred from spactl's exit. With different bytes the
/// winner's release is activated for the fragment, its URL read through the tunnel, and the fragment's
/// earlier release activated again under a bound of its own (#1411's decision of 2026-11-24 on the cell's
/// levers); with the same bytes both runs must complete.
/// </summary>
public sealed class PublishRaceDrive(PublishRaceSetup setup)
{
    public static readonly IReadOnlyList<string> Variants = ["different", "same"];

    /// <summary>How long a run waits at the barrier for the other before it stops.</summary>
    public static readonly TimeSpan Barrier = TimeSpan.FromSeconds(60);

    /// <summary>How long the restore of the fragment's earlier release may take, whatever the row's token says.</summary>
    public static readonly TimeSpan Restore = TimeSpan.FromMinutes(5);

    /// <summary>How often the winner's URL is read until it answers, up to the cell's registry propagation and a margin.</summary>
    public static readonly TimeSpan ServedPoll = TimeSpan.FromSeconds(5);

    public async Task<PublishRaceRecord> RunAsync(string appId, LiveFragment fragment, string stamp, CancellationToken ct)
    {
        List<PublishRaceDrive.Variant> variants = [];
        foreach (string variant in Variants)
        {
            variants.Add(await PublishAsync(appId, fragment, stamp, variant, ct));
        }

        PublishRaceDrive.Variant different = variants[0];
        PublishRun[] completed = [.. different.Race.Runs.Where(run => run.Completed == true)];
        if (completed.Length != 1)
        {
            return new PublishRaceRecord([.. variants.Select(each => each.Race)]);
        }

        int won = Array.IndexOf([.. different.Race.Runs], completed[0]);
        string winner = different.Releases[won];
        string loser = different.Releases[1 - won];
        string? storedSha = await Sha256Async(setup.Store, Namespace(appId, fragment.FragmentId) + $"releases/{winner}/{different.File}", ct);

        // #1005 F-T5: "the loser has no object for the key". The record holds one stored digest, so a loser's object
        // is recorded in its place, and the observer fails a key holding bytes other than the winner's.
        if (await Sha256Async(setup.Store, Namespace(appId, fragment.FragmentId) + $"releases/{loser}/{different.File}", ct) is { } loserSha)
        {
            storedSha = loserSha;
        }

        string? servedSha = await ServeWinnerAsync(appId, fragment, winner, different.File, ct);
        variants[0] = different with { Race = different.Race with { StoredSha256 = storedSha, ServedSha256 = servedSha } };
        return new PublishRaceRecord([.. variants.Select(each => each.Race)]);
    }

    private sealed record Variant(PublishRace Race, string File, string[] Releases);

    private async Task<PublishRaceDrive.Variant> PublishAsync(string appId, LiveFragment fragment, string stamp, string variant, CancellationToken ct)
    {
        string file = $"assets/race-{stamp}-{variant}.js";
        byte[][] bytes = variant == "same"
            ? [Module(stamp, variant, "a"), Module(stamp, variant, "a")]
            : [Module(stamp, variant, "a"), Module(stamp, variant, "b")];
        string[] releases = [$"race-{stamp}-{variant}-a", $"race-{stamp}-{variant}-b"];
        string root = Directory.CreateTempSubdirectory("spa-e2e-race-").FullName;
        try
        {
            ClaimHold hold = new(setup.Store, Namespace(appId, fragment.FragmentId) + "claims/" + file, 2, Barrier);
            string[] directories = [.. bytes.Select((body, i) => Release(root, releases[i], fragment.Entry, file, body))];
            int?[] exits = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => setup.Statuses.WithinAsync(releases[i], () => setup.Spactl(
                ["fragment", "publish", appId, fragment.FragmentId, directories[i], releases[i], "--entry", fragment.Entry, "--outputs", setup.OutputsPath], hold, ct))));
            string claim = hold.ClaimKey;
            PublishRun[] runs = [.. exits.Select((exit, i) => new PublishRun(
                exit is null ? null : exit == 0,
                exit is 0 or null ? null : setup.Statuses.RefusalOf(claim, releases[i]),
                Convert.ToHexStringLower(SHA256.HashData(bytes[i]))))];
            return new PublishRaceDrive.Variant(new PublishRace(variant, runs, null, null), file, releases);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The winner activated for the fragment, its URL read until it answers, and the earlier release activated again.</summary>
    private Task<string?> ServeWinnerAsync(string appId, LiveFragment fragment, string winner, string file, CancellationToken ct) =>
        Restoring.RunAsync(
            async () =>
            {
                if (await setup.Spactl(Activate(appId, fragment.FragmentId, winner), setup.Store, ct) != 0)
                {
                    return null;
                }

                DateTimeOffset until = DateTimeOffset.UtcNow + setup.Propagation + TimeSpan.FromSeconds(30);
                Uri url = setup.Shell.Url($"/fragments/{fragment.FragmentId}/{file}");
                while (true)
                {
                    (int status, byte[] body) = await setup.Read(url, ct);
                    if (status == 200)
                    {
                        return Convert.ToHexStringLower(SHA256.HashData(body));
                    }

                    if (DateTimeOffset.UtcNow >= until)
                    {
                        return (string?)null;
                    }

                    await setup.Delay(ServedPoll, ct);
                }
            },
            async bound =>
            {
                int? restored = await setup.Spactl(Activate(appId, fragment.FragmentId, fragment.Release), setup.Store, bound);
                if (restored != 0)
                {
                    throw new InvalidOperationException(
                        $"the restore of {appId}/{fragment.FragmentId} to release {fragment.Release} exited {restored?.ToString(CultureInfo.InvariantCulture) ?? "by an exception"}: activate it by hand before the next row.");
                }
            },
            Restore);

    private string[] Activate(string appId, string fragmentId, string release) =>
        ["fragment", "activate", appId, fragmentId, release, "--config", setup.PlatformConfig, "--outputs", setup.OutputsPath];

    /// <summary>A module no other run publishes: the stamp, the variant and the side.</summary>
    private static byte[] Module(string stamp, string variant, string side) =>
        Encoding.UTF8.GetBytes($"export const race = \"{stamp}-{variant}-{side}\";\n");

    /// <summary>A minimal fragment release: the entry, re-exporting nothing, and the racing asset.</summary>
    private static string Release(string root, string release, string entry, string file, byte[] body)
    {
        string directory = Path.Combine(root, release);
        Directory.CreateDirectory(Path.Combine(directory, "assets"));
        File.WriteAllText(Path.Combine(directory, entry), "export {};\n");
        File.WriteAllBytes(Path.Combine(directory, file), body);
        return directory;
    }

    /// <summary>contracts/s3-layout.md: one fragment's namespace, under its shell's.</summary>
    public static string Namespace(string appId, string fragmentId) => $"apps/{appId}/fragments/{fragmentId}/";

    private static async Task<string?> Sha256Async(IObjectStore store, string key, CancellationToken ct)
    {
        StoredObject? stored = await store.GetAsync(key, ifNoneMatch: null, ct);
        if (stored?.Body is not { } body)
        {
            return null;
        }

        await using (body)
        {
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(body, ct));
        }
    }
}

/// <summary>What F-T5 drives through: spactl in this process, the cell's bucket, S3's statuses, the shell's host through the tunnel, and the cell's names.</summary>
public sealed record PublishRaceSetup(
    SpactlRun Spactl,
    IObjectStore Store,
    S3Statuses Statuses,
    HarnessAppUrl Shell,
    Func<Uri, CancellationToken, Task<(int Status, byte[] Body)>> Read,
    string OutputsPath,
    string PlatformConfig,
    TimeSpan Propagation)
{
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>The live path: spactl's own environment with its store replaced, over an S3 client whose responses <paramref name="statuses"/> sees.</summary>
    public static (SpactlRun Spactl, IObjectStore Store) Live(Cell cell, S3Statuses statuses)
    {
        AmazonS3Client client = new(Config(cell.Region, statuses));
        return (InProcess, new S3ObjectStore(client, cell.Bucket));
    }

    /// <summary>The S3 client's configuration: the cell's Region, and its HTTP clients from <paramref name="statuses"/>, which is how a refusal's status is seen.</summary>
    public static AmazonS3Config Config(string region, S3Statuses statuses) =>
        new() { RegionEndpoint = RegionEndpoint.GetBySystemName(region), HttpClientFactory = statuses };

    /// <summary>
    /// <see cref="SpactlApp.RunAsync"/> over the given store, with spactl's own history and HTTP handler, and its
    /// output kept from the test's: it names keys and releases only, but a row's output is its verdict.
    /// </summary>
    private static async Task<int?> InProcess(IReadOnlyList<string> arguments, IObjectStore store, CancellationToken ct)
    {
        SpactlEnvironment live = SpactlEnvironment.Live();
        try
        {
            return await SpactlApp.RunAsync(arguments, new SpactlEnvironment
            {
                Store = _ => store,
                History = live.History,
                Http = live.Http,
                Out = TextWriter.Null,
                Error = TextWriter.Null,
            }, ct);
        }
        catch (Exception stopped) when (stopped is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The type alone: an SDK or barrier message may name a key, never a secret, but a row's output is its verdict.
            Console.WriteLine($"spactl {string.Join(' ', arguments.Take(2))} stopped with {stopped.GetType().Name}");
            return null;
        }
    }
}

/// <summary>An app's origin, for the one URL F-T5 reads.</summary>
public sealed record HarnessAppUrl(Uri Origin)
{
    public Uri Url(string path) => new(Origin, path);
}

/// <summary>
/// Holds the claim write of one key until <see cref="Parties"/> publishes have reached it, then lets them
/// race: F-T2's barrier before the claim. Every other call goes straight to the store. A run that waits
/// past the barrier's bound stops with <see cref="TimeoutException"/>.
/// </summary>
public sealed class ClaimHold(IObjectStore inner, string claimKey, int parties, TimeSpan wait) : IObjectStore
{
    private readonly TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int arrived;

    public string ClaimKey => claimKey;

    public int Parties => parties;

    public int Arrived => Volatile.Read(ref arrived);

    public Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct) => inner.GetAsync(key, ifNoneMatch, ct);

    public async Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct)
    {
        if (key == claimKey)
        {
            if (Interlocked.Increment(ref arrived) >= parties)
            {
                all.TrySetResult();
            }

            await all.Task.WaitAsync(wait, ct);
        }

        return await inner.PutAsync(key, body, ifMatch, ct);
    }
}

/// <summary>
/// The status S3 answered each <c>PUT</c> with, seen at the SDK's HTTP client
/// (<see cref="ClientConfig.HttpClientFactory"/>): S3 declines a conditional write with 412 when the
/// condition fails and 409 <c>ConditionalRequestConflict</c> when another conditional write to the key is
/// in flight (https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes.html), and
/// <see cref="S3ObjectStore"/> turns both into false, so only the response says which.
/// </summary>
public sealed class S3Statuses : HttpClientFactory
{
    private readonly ConcurrentQueue<(string Key, string? Run, int Status)> puts = new();
    private readonly AsyncLocal<string?> run = new();
    private readonly Func<HttpMessageHandler> inner;

    public S3Statuses()
        : this(() => new SocketsHttpHandler())
    {
    }

    public S3Statuses(Func<HttpMessageHandler> inner) => this.inner = inner;

    public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(new Tap(this, inner()));

    /// <summary>
    /// Runs <paramref name="action"/> as <paramref name="name"/>'s: every PUT its flow sends is recorded under
    /// that name, since the SDK's handler runs on the caller's async flow.
    /// </summary>
    public async Task<T> WithinAsync<T>(string name, Func<Task<T>> action)
    {
        run.Value = name;
        try
        {
            return await action();
        }
        finally
        {
            run.Value = null;
        }
    }

    /// <summary>The refusal S3 answered <paramref name="name"/>'s PUT of the claim key with, 412 or 409; null when no such answer was seen.</summary>
    public int? RefusalOf(string claimKey, string name) =>
        puts.LastOrDefault(put => put.Key == claimKey && put.Run == name
            && put.Status is (int)HttpStatusCode.PreconditionFailed or (int)HttpStatusCode.Conflict) is { Key: not null } seen
            ? seen.Status
            : null;

    /// <summary>Records one PUT: its key, the run whose flow sent it, and S3's status.</summary>
    public void Saw(string key, int status) => puts.Enqueue((key, run.Value, status));

    private sealed class Tap(S3Statuses statuses, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Put && request.RequestUri is { } uri)
            {
                statuses.Saw(Key(uri), (int)response.StatusCode);
            }

            return response;
        }

        /// <summary>The object key: the path after the bucket in path style, the whole path in virtual-hosted style (the SDK's default).</summary>
        private static string Key(Uri uri)
        {
            string path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            int apps = path.IndexOf("apps/", StringComparison.Ordinal);
            return apps > 0 ? path[apps..] : path;
        }
    }
}
