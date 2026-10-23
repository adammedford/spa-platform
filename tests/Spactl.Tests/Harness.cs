using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Platform.Abstractions;
using Platform.Testing;
using Spactl;

namespace Spactl.Tests;

/// <summary>
/// One spactl invocation's world: the versioned in-memory store for every bucket, a <c>/readyz</c> that
/// answers from what the store holds (a gateway that has polled), and the two output streams. Files
/// the test writes go under the test's own output directory, inside the worktree.
/// </summary>
internal sealed class Harness : IDisposable
{
    public const string RegistryKey = "registry/registry.json";

    private readonly string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N"));

    public Harness()
    {
        Directory.CreateDirectory(scratch);
        Readyz = new ReadyzStub(Store);
        ConfigPath = Example("platform-config.example.json");
        OutputsPath = Example("infra-outputs.example.json");
    }

    public VersionedStore Store { get; } = new();

    public ReadyzStub Readyz { get; }

    /// <summary>
    /// The clock the command reads its deadline from. It stands still unless a test moves it, as
    /// <see cref="ReadyzStub.Clock"/> does once per read.
    /// </summary>
    public FakeTimeProvider Clock { get; } = TestClock.New();

    /// <summary>When set, the store commands see instead of <see cref="Store"/>.</summary>
    public IObjectStore? StoreOverride { get; set; }

    public StringWriter Out { get; private set; } = new();

    public StringWriter Error { get; private set; } = new();

    /// <summary>Every bucket the command asked for a store over.</summary>
    public List<string> Buckets { get; } = [];

    public string ConfigPath { get; }

    public string OutputsPath { get; set; }

    public static string Example(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "examples", .. parts]);

    public static JsonObject ExampleJson(params string[] parts) =>
        JsonNode.Parse(File.ReadAllText(Example(parts)))!.AsObject();

    /// <summary>
    /// Runs spactl with fresh output streams, passing the outputs path and, to every command that reads
    /// one, the config path (spactl refuses an option its command does not read).
    /// </summary>
    public Task<int> Run(params string[] args) =>
        args is ["release", "publish", ..]
            ? RunRaw([.. args, "--outputs", OutputsPath])
            : RunRaw([.. args, "--config", ConfigPath, "--outputs", OutputsPath]);

    public Task<int> RunRaw(params string[] args)
    {
        Out = new StringWriter();
        Error = new StringWriter();
        SpactlEnvironment environment = new()
        {
            Store = location =>
            {
                Buckets.Add(location.Bucket);
                return StoreOverride ?? Store;
            },
            History = _ => Store,
            Http = Readyz,
            Out = Out,
            Error = Error,
            PollInterval = TimeSpan.Zero,
            Time = Clock,
        };
        return SpactlApp.RunAsync(args, environment, TestContext.Current.CancellationToken);
    }

    /// <summary>Writes a file under this test's scratch directory and returns its path.</summary>
    public string WriteFile(string name, string text)
    {
        string path = Path.Combine(scratch, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public string ScratchDirectory(string name)
    {
        string path = Path.Combine(scratch, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task<JsonObject> LiveRegistry() =>
        JsonNode.Parse(await Store.TextAsync(RegistryKey) ?? throw new InvalidOperationException("No registry."))!.AsObject();

    public void Dispose()
    {
        try
        {
            Directory.Delete(scratch, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory under bin/ is harmless.
        }
    }
}

/// <summary>
/// <c>/readyz</c> as the gateway answers it (contracts/pipeline.md, Health endpoints): 503 with no body
/// until a snapshot is live, then 200 with <c>{"revision": N, "instance": "stub"}</c>. The revision is the
/// live registry object's, so a test sees what a gateway that has polled would report. <see cref="Lag"/>
/// answers 503 that many times first, a gateway that has not polled yet.
///
/// <see cref="Script"/>, when a test fills it, answers instead, in turn and round again, as an ALB
/// spreads reads over several instances (change request P2-5).
/// </summary>
internal sealed class ReadyzStub(VersionedStore store) : HttpMessageHandler
{
    /// <summary>The instance the store-backed answer names.</summary>
    public const string Instance = "stub";

    public int Lag { get; set; }

    public List<Uri> Requests { get; } = [];

    public List<ReadyzAnswer> Script { get; } = [];

    /// <summary>When set, moved on a second after each read, so a deadline passes after a few reads.</summary>
    public FakeTimeProvider? Clock { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        Clock?.Advance(TimeSpan.FromSeconds(1));
        if (request.RequestUri!.AbsolutePath != "/readyz")
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (Script.Count > 0)
        {
            return Script[(Requests.Count - 1) % Script.Count].Response();
        }

        string? live = await store.TextAsync(Harness.RegistryKey);
        if (Lag > 0 || live is null)
        {
            Lag--;
            return ReadyzAnswer.NotReady.Response();
        }

        return ReadyzAnswer.At(Instance, JsonNode.Parse(live)!["revision"]!.GetValue<long>()).Response();
    }
}

/// <summary>One <c>/readyz</c> answer: an instance at a revision, or the bodiless 503.</summary>
internal sealed record ReadyzAnswer(string? Instance, long? Revision)
{
    public static readonly ReadyzAnswer NotReady = new(null, null);

    public static ReadyzAnswer At(string instance, long revision) => new(instance, revision);

    public HttpResponseMessage Response() =>
        Revision is null
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"revision\":" + Revision + ",\"instance\":\"" + Instance + "\"}", Encoding.UTF8, "application/json"),
            };
}
