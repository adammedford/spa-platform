using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MockApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Platform.Abstractions;
using Platform.Registry;
using Platform.Testing;
using TestIdp;

namespace Gateway.TestHarness;

/// <summary>
/// The hermetic cell (task 6.1): the real gateway composition, and everything it talks to, in one test
/// process.
///
/// <list type="bullet">
/// <item>The gateway, started from its own <c>Program.cs</c> on Kestrel (<see cref="GatewayFactory"/>),
/// with the issuer in its configuration pointing at the test identity provider.</item>
/// <item>The real registry source and poller over an <see cref="InMemoryObjectStore"/>, which also holds
/// every release, as the cell's one bucket does. Client secrets come from a
/// <see cref="FakeClientSecretSource"/> holding the same secrets the provider's clients were registered
/// with.</item>
/// <item>The test identity provider (task 5.4) on a loopback port, with one client per app.</item>
/// <item>The real mock API (task 5.3) on two loopback listeners, <c>orders</c> and <c>reports</c>,
/// validating tokens against the test provider and admitting app 1's client and app 2's client
/// respectively, as in the cell.</item>
/// <item>An echo upstream on a loopback listener, listed in every app as <see cref="GatewayHarnessOptions.EchoApi"/>.</item>
/// </list>
///
/// A test talks to it with <see cref="CreateBrowser"/>, or with <see cref="RawHttp"/> against
/// <see cref="GatewayAddress"/> for a request no browser sends. The registry is changed while the
/// gateway runs with <see cref="UpdateRegistryAsync"/>, which writes the object and then polls once, so
/// a test never waits out the poll interval.
/// </summary>
public sealed class GatewayHarness : IAsyncDisposable
{
    /// <summary>The registry object's key, as contracts/s3-layout.md fixes it.</summary>
    public const string RegistryKey = "registry/registry.json";

    /// <summary>
    /// The background poll interval. Long on purpose: the harness polls itself after every write, and a
    /// poll in the background would make the order of a test's log lines depend on timing.
    /// </summary>
    public const int PollSeconds = 3600;

    private readonly GatewayFactory factory;
    private readonly EchoUpstreamListener echoListener;
    private readonly Dictionary<string, HarnessApp> apps = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim registryWrites = new(1, 1);
    private JsonObject document;

    private GatewayHarness(
        GatewayHarnessOptions options,
        TestIdentityProvider idp,
        MockApiHost mockApi,
        EchoUpstream echo,
        EchoUpstreamListener echoListener,
        InMemoryObjectStore store,
        FakeClientSecretSource secrets,
        CapturedLog log,
        GatewayFactory factory,
        JsonObject document)
    {
        Options = options;
        Idp = idp;
        MockApi = mockApi;
        Echo = echo;
        this.echoListener = echoListener;
        Store = store;
        Secrets = secrets;
        Log = log;
        this.factory = factory;
        this.document = document;
    }

    public GatewayHarnessOptions Options { get; }

    /// <summary>The test identity provider. Its fault switches are live.</summary>
    public TestIdentityProvider Idp { get; }

    /// <summary>The mock API. <c>AdmissionOf("orders").Admit(clientId)</c> is how F-07 admits app 3.</summary>
    public MockApiHost MockApi { get; }

    /// <summary>The echo upstream's recorder: every request that reached it.</summary>
    public EchoUpstream Echo { get; }

    /// <summary>Where the echo upstream listens.</summary>
    public Uri EchoAddress => echoListener.BaseAddress;

    /// <summary>The cell's bucket: the registry object and every release.</summary>
    public InMemoryObjectStore Store { get; }

    /// <summary>The client secrets the gateway reads. Set a new reference here before publishing it (F-09).</summary>
    public FakeClientSecretSource Secrets { get; }

    /// <summary>Every line the gateway logged, as its formatter wrote it.</summary>
    public CapturedLog Log { get; }

    /// <summary>The gateway's services.</summary>
    public IServiceProvider Services => factory.Services;

    /// <summary>The gateway's own listener, e.g. <c>http://127.0.0.1:54321/</c>, for <see cref="RawHttp"/>.</summary>
    public Uri GatewayAddress => factory.Address;

    /// <summary>The live registry, as stage 4 reads it.</summary>
    public IRegistry Registry => Services.GetRequiredService<IRegistry>();

    /// <summary>Every app the harness has registered with the provider, by appId, whether or not it is in the registry now.</summary>
    public IReadOnlyDictionary<string, HarnessApp> Apps => apps;

    /// <summary>A copy of the registry document last written.</summary>
    public JsonObject RegistryDocument => (JsonObject)document.DeepClone();

    /// <summary>The app with this appId.</summary>
    public HarnessApp App(string appId) =>
        apps.TryGetValue(appId, out HarnessApp? app) ? app : throw new KeyNotFoundException($"the harness has no app {appId}.");

    public static async Task<GatewayHarness> StartAsync(GatewayHarnessOptions? options = null, CancellationToken ct = default)
    {
        options ??= new GatewayHarnessOptions();

        TestIdentityProvider idp = new(options.Clock);
        idp.EndSessionStyle = options.Profile == "cognito" ? EndSessionStyle.Cognito : EndSessionStyle.Standard;
        await idp.StartAsync(ct);

        // The two listeners the plan fixes, on ports the system picks so suites can run side by side.
        MockApiOptions fixedPair = MockApiOptions.Default(idp.Issuer, idp.DiscoveryUri, ClientIdFor("app1"), ClientIdFor("app2"));
        MockApiHost mockApi = await MockApiHost.StartAsync(
            fixedPair with { Listeners = [.. fixedPair.Listeners.Select(listener => listener with { Port = 0 })] }, ct);

        EchoUpstream echo = new();
        EchoUpstreamListener echoListener = await echo.ListenAsync(ct);

        InMemoryObjectStore store = new();
        FakeClientSecretSource secrets = new();
        CapturedLog log = new();

        Dictionary<string, string> upstreams = new(StringComparer.Ordinal)
        {
            ["orders"] = Origin(mockApi.BaseAddressOf("orders")),
            ["reports"] = Origin(mockApi.BaseAddressOf("reports")),
        };
        if (options.EchoApi is { } echoApi)
        {
            upstreams[echoApi] = Origin(echoListener.BaseAddress);
        }

        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Platform:Cell"] = "harness",
            ["Registry:Bucket"] = "harness-bucket",
            ["Registry:Key"] = RegistryKey,
            ["Registry:PollSeconds"] = PollSeconds.ToString(CultureInfo.InvariantCulture),
            ["Assets:Bucket"] = "harness-bucket",
            [$"Issuers:{options.IssuerName}:Authority"] = idp.BaseUrl,
            [$"Issuers:{options.IssuerName}:Profile"] = options.Profile,
            [$"Issuers:{options.IssuerName}:BrowserOrigin"] = idp.BaseUrl,
            ["Policy:AllowedHostSuffixes:0"] = TestApps.HostSuffix + ":8443",
            ["Session:Store"] = "memory",
            ["Session:MaxSessions"] = "10000",
        };
        int index = 0;
        foreach (string upstream in upstreams.Values.Distinct(StringComparer.Ordinal))
        {
            settings[$"Policy:AllowedUpstreams:{index++}"] = upstream;
        }

        for (int i = 0; i < options.TrustedNetworks.Count; i++)
        {
            settings[$"ForwardedHeaders:TrustedNetworks:{i}"] = options.TrustedNetworks[i];
        }

        foreach ((string key, string? value) in options.Configuration)
        {
            settings[key] = value;
        }

        GatewayFactory factory = new(settings, services =>
        {
            services.RemoveAll<IObjectStore>();
            services.AddSingleton<IObjectStore>(store);
            services.RemoveAll<IClientSecretSource>();
            services.AddSingleton<IClientSecretSource>(secrets);
            if (options.Clock is { } clock)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
            }

            // Beside the console logger, rendering with the formatter the gateway configured for it.
            services.AddSingleton<ILoggerProvider>(provider => new CapturedLog.Provider(log, () =>
                provider.GetServices<ConsoleFormatter>().FirstOrDefault(formatter =>
                    formatter.Name == provider.GetService<Microsoft.Extensions.Options.IOptionsMonitor<ConsoleLoggerOptions>>()?.CurrentValue.FormatterName)));

            options.ConfigureServices?.Invoke(services);
        });

        GatewayHarness harness = new(
            options, idp, mockApi, echo, echoListener, store, secrets, log, factory,
            new JsonObject { ["schemaVersion"] = 1, ["revision"] = 0, ["apps"] = new JsonArray() });
        harness.upstreamsByApi = upstreams;

        try
        {
            // The first revision is in the bucket before the gateway starts, as it is when a task starts
            // in the cell: the poller's first read finds it.
            JsonArray entries = [];
            foreach (string appId in options.Apps)
            {
                HarnessApp app = harness.Register(appId);
                await harness.PublishReleaseAsync(appId, options.Release, FixtureFiles.Spa(appId), ct);
                entries.Add(harness.EntryFor(app, options.Release));
            }

            JsonObject first = new() { ["schemaVersion"] = 1, ["revision"] = 1, ["apps"] = entries };
            if (!await store.PutAsync(RegistryKey, Utf8(first.ToJsonString()), ifMatch: null, ct))
            {
                throw new InvalidOperationException("the registry object already exists in a new store.");
            }

            harness.document = first;

            factory.StartServer();

            // The poller's first read may still be in flight. One more poll waits for it (the source
            // runs one poll at a time) and settles the outcome either way.
            await harness.RefreshRegistryAsync(ct);
            if (harness.Registry.Current.Revision != 1)
            {
                throw new InvalidOperationException(
                    "the first registry revision did not go live. The gateway log says why:" + Environment.NewLine
                    + string.Join(Environment.NewLine, log.Lines.TakeLast(20)));
            }
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }

        return harness;
    }

    /// <summary>A browser whose network reaches the gateway through the stand-in load balancer.</summary>
    public TestBrowser CreateBrowser() => new(new LoadBalancerTransport(GatewayAddress));

    /// <summary>Polls the registry object once, as the poller would on its next tick.</summary>
    public Task<RefreshOutcome> RefreshRegistryAsync(CancellationToken ct = default) =>
        Services.GetRequiredService<RegistrySource>().RefreshAsync(ct);

    /// <summary>
    /// Copies the last document written, raises its revision by one, lets <paramref name="edit"/> change it,
    /// writes it and polls once. The outcome says whether it went live.
    /// </summary>
    public async Task<RefreshOutcome> UpdateRegistryAsync(Action<JsonObject> edit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        JsonObject next = RegistryDocument;
        next["revision"] = long.Parse(next["revision"]!.ToJsonString(), CultureInfo.InvariantCulture) + 1;
        edit(next);
        return await PublishRegistryAsync(next, ct);
    }

    /// <summary>Writes <paramref name="next"/> as the registry object, as it stands, and polls once.</summary>
    public async Task<RefreshOutcome> PublishRegistryAsync(JsonObject next, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(next);
        RefreshOutcome outcome = await PublishRegistryTextAsync(next.ToJsonString(), ct);
        document = (JsonObject)next.DeepClone();
        return outcome;
    }

    /// <summary>
    /// Writes any text as the registry object, conditional on the version in the bucket as <c>spactl</c>
    /// writes it, and polls once. For a document that is not valid JSON, or not valid at all (F-09, N-14).
    /// <see cref="RegistryDocument"/> is not changed: the next <see cref="UpdateRegistryAsync"/> starts from
    /// the last document written as JSON.
    /// </summary>
    public async Task<RefreshOutcome> PublishRegistryTextAsync(string text, CancellationToken ct = default)
    {
        await registryWrites.WaitAsync(ct);
        try
        {
            StoredObject current = await Store.GetAsync(RegistryKey, ifNoneMatch: null, ct)
                ?? throw new InvalidOperationException("the registry object is missing.");
            current.Body?.Dispose();
            if (!await Store.PutAsync(RegistryKey, Utf8(text), current.ETag, ct))
            {
                throw new InvalidOperationException("the registry object changed under the harness.");
            }
        }
        finally
        {
            registryWrites.Release();
        }

        return await RefreshRegistryAsync(ct);
    }

    /// <summary>Writes one release of an app into the bucket under <c>apps/{appId}/releases/{release}/</c>.</summary>
    public async Task PublishReleaseAsync(string appId, string release, IReadOnlyDictionary<string, byte[]> files, CancellationToken ct = default)
    {
        foreach ((string path, byte[] bytes) in files)
        {
            if (!await Store.PutAsync($"apps/{appId}/releases/{release}/{path}", new MemoryStream(bytes), ifMatch: null, ct))
            {
                throw new InvalidOperationException($"apps/{appId}/releases/{release}/{path} already exists: a release is written once.");
            }
        }
    }

    /// <summary>
    /// Onboards an app while the gateway runs, the hermetic half of F-07: a client at the provider, its
    /// secret where the gateway reads it, its release in the bucket and its entry in a new registry
    /// revision. The mock API does not admit its client: that is the API owner's decision, which the test
    /// takes with <c>MockApi.AdmissionOf(name).Admit(app.ClientId)</c>.
    /// </summary>
    public async Task<(HarnessApp App, RefreshOutcome Outcome)> OnboardAsync(string appId, CancellationToken ct = default)
    {
        HarnessApp app = apps.TryGetValue(appId, out HarnessApp? known) ? known : Register(appId);
        await PublishReleaseAsync(appId, Options.Release, FixtureFiles.Spa(appId), ct);
        RefreshOutcome outcome = await UpdateRegistryAsync(next => next["apps"]!.AsArray().Add(EntryFor(app, Options.Release)), ct);
        return (app, outcome);
    }

    /// <summary>
    /// The registry entry for an app: its fixture definition, with what onboarding fills in (the release,
    /// the client id and the secret reference), the issuer named as the harness configures it, and each
    /// mock API upstream pointed at the listener the harness started.
    /// </summary>
    public JsonObject EntryFor(HarnessApp app, string release)
    {
        ArgumentNullException.ThrowIfNull(app);
        JsonObject entry = FixtureFiles.AppDefinition(app.AppId);
        entry["release"] = release;

        JsonObject oauth = entry["oauth"]!.AsObject();
        oauth["issuer"] = Options.IssuerName;
        oauth["clientId"] = app.ClientId;
        oauth["clientSecretRef"] = app.SecretRef;

        JsonArray apis = entry["apis"] as JsonArray ?? [];
        foreach (JsonObject api in apis.OfType<JsonObject>())
        {
            if (api["name"]?.GetValue<string>() is { } name && upstreamsByApi.TryGetValue(name, out string? upstream))
            {
                api["upstream"] = upstream;
            }
        }

        if (Options.EchoApi is { } echoApi && !apis.OfType<JsonObject>().Any(api => api["name"]?.GetValue<string>() == echoApi))
        {
            apis.Add(new JsonObject { ["name"] = echoApi, ["upstream"] = upstreamsByApi[echoApi], ["timeoutSeconds"] = 30 });
        }

        entry["apis"] = apis;
        return entry;
    }

    public async ValueTask DisposeAsync()
    {
        await factory.DisposeAsync();
        await MockApi.DisposeAsync();
        await echoListener.DisposeAsync();
        await Idp.DisposeAsync();
        registryWrites.Dispose();
    }

    private Dictionary<string, string> upstreamsByApi = new(StringComparer.Ordinal);

    /// <summary>Gives an app a client at the provider and its secret where the gateway reads it.</summary>
    private HarnessApp Register(string appId)
    {
        string host = FixtureFiles.AppDefinition(appId)["host"]!.GetValue<string>();
        HarnessApp app = new(
            appId,
            host,
            new Uri("https://" + host + "/"),
            ClientIdFor(appId),
            // Random per run, so a secret seen in a log line is this run's and no other's (N-16).
            "secret-" + appId + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            $"ssm:/spa-platform/harness/apps/{appId}/client-secret:1");

        Idp.AddClient(new TestIdpClient(
            app.ClientId,
            app.ClientSecret,
            new HashSet<string>(StringComparer.Ordinal) { app.Url("/signin-oidc").ToString() },
            // The standard profile returns to the signout callback; the Cognito profile lands on / (ADR-0011).
            new HashSet<string>(StringComparer.Ordinal) { app.Url("/signout-callback-oidc").ToString(), app.Url("/").ToString() }));
        Secrets[app.SecretRef] = app.ClientSecret;

        apps[appId] = app;
        return app;
    }

    /// <summary>The client id the provider issues an app. Fixed per appId, so the mock API can admit it before it exists.</summary>
    public static string ClientIdFor(string appId) => "client-" + appId;

    private static string Origin(Uri address) => address.GetLeftPart(UriPartial.Authority);

    private static MemoryStream Utf8(string text) => new(Encoding.UTF8.GetBytes(text));
}

/// <summary>One app as the harness set it up: its host, and its client at the test provider.</summary>
public sealed record HarnessApp(
    string AppId,
    string Host,
    Uri Origin,
    string ClientId,
    string ClientSecret,
    string SecretRef)
{
    /// <summary>A URL on the app's host.</summary>
    public Uri Url(string path) => new(Origin, path);

    /// <summary>Names the app without its secret, so it is safe in assertion output.</summary>
    public override string ToString() => $"{AppId} ({Host}, {ClientId})";
}
