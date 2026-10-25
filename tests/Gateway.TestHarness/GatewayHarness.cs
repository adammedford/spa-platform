using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MockApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Amazon.SimpleSystemsManagement;
using Platform.Abstractions;
using Platform.Hosting;
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
///
/// <para>The gateway requires a session before it serves an asset or forwards an API call, as ADR-0008
/// decides, because the composition that ships does: <c>AddPlatformIdentity</c> binds the token source
/// the proxy's token step asks, and <c>UsePlatformShellGate</c> is always in the pipeline. The harness
/// adds nothing to either and has no switch to serve without a session, so a test signs a browser in
/// with <see cref="SignInAsync"/>, and a row about a request without a session asserts the refusal that
/// request gets (task 6.3c).</para>
///
/// <para>One host by default, as every Phase 1 suite runs. <see cref="GatewayHarnessOptions.TwoHosts"/>
/// starts two (plan row P2-5, #345): the real key ring over one Parameter Store stub, one session store,
/// one lease table and a deterministic instance id each, behind a balancer that alternates between them.
/// Every registry write polls the first host only, so a test leaves the others behind until it polls
/// them itself (<see cref="GatewayHost.RefreshRegistryAsync"/>).</para>
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

    private readonly List<GatewayHost> hosts = [];
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
        this.document = document;
    }

    /// <summary>Builds the next host's factory over the harness's shared parts; set once in <see cref="StartAsync"/>.</summary>
    private Func<int, (GatewayFactory Factory, string? Instance, ParameterStoreStub? Store)> newHost = null!;

    private int nextTarget = -1;

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

    /// <summary>Every line any gateway host logged, as its formatter wrote it.</summary>
    public CapturedLog Log { get; }

    /// <summary>
    /// The gateway hosts, in the order they started (#345). The first is the one every single-host member
    /// below names, and the only one <see cref="UpdateRegistryAsync"/> and its siblings poll.
    /// </summary>
    public IReadOnlyList<GatewayHost> Hosts => hosts;

    /// <summary>The cell's one session store, every host's, or null under <see cref="KeyRingSharing.FrameworkDefault"/>.</summary>
    public SwitchableSessionStore? SessionStore { get; private set; }

    /// <summary>The cell's one refresh-lease table, every host's, or null under <see cref="KeyRingSharing.FrameworkDefault"/>.</summary>
    public IRefreshLeases? RefreshLeases { get; private set; }

    /// <summary>The first gateway host's services.</summary>
    public IServiceProvider Services => hosts[0].Services;

    /// <summary>The first gateway host's own listener, e.g. <c>http://127.0.0.1:54321/</c>, for <see cref="RawHttp"/>.</summary>
    public Uri GatewayAddress => hosts[0].Address;

    /// <summary>The first gateway host's live registry, as its host resolution reads it.</summary>
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
            ["orders"] = FetchMetadata.Origin(mockApi.BaseAddressOf("orders")),
            ["reports"] = FetchMetadata.Origin(mockApi.BaseAddressOf("reports")),
        };
        if (options.EchoApi is { } echoApi)
        {
            upstreams[echoApi] = FetchMetadata.Origin(echoListener.BaseAddress);
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

        bool cell = options.KeyRing != KeyRingSharing.FrameworkDefault;
        if (options.Hosts < 1 || (options.Hosts > 1 && !cell))
        {
            throw new ArgumentException(
                "a harness has at least one host, and more than one only over a key ring it sets (GatewayHarnessOptions.KeyRing).",
                nameof(options));
        }

        if (cell)
        {
            // The real ring's section (plan row P2-2): the prefix only, so the package uses the account's
            // default key, which the stub never checks.
            settings["DataProtection:ParameterPrefix"] = "/spa-platform/harness/data-protection/";
        }

        SwitchableSessionStore? sessionStore = cell ? new SwitchableSessionStore() : null;
        IRefreshLeases? leases = cell ? new InMemoryRefreshLeases(options.Clock ?? TimeProvider.System) : null;
        ParameterStoreStub? sharedParameterStore = options.KeyRing == KeyRingSharing.OneParameterStore ? new ParameterStoreStub() : null;

        (GatewayFactory, string?, ParameterStoreStub?) NewHost(int index)
        {
            string? instance = cell ? options.InstanceIds[index] : null;
            ParameterStoreStub? parameterStore = options.KeyRing switch
            {
                KeyRingSharing.OneParameterStore => sharedParameterStore,
                KeyRingSharing.ParameterStorePerHost => new ParameterStoreStub(),
                _ => null,
            };

            GatewayFactory hostFactory = new(settings, services => ConfigureHost(services, instance, parameterStore));
            return (hostFactory, instance, parameterStore);
        }

        void ConfigureHost(IServiceCollection services, string? instance, ParameterStoreStub? parameterStore)
        {
            if (parameterStore is not null)
            {
                StubUnderTheRing(services, parameterStore);
            }

            // The harness runs every registry poll itself (RefreshRegistryAsync). With more than one host a
            // background poll is the one thing that could move a host the test left behind: the poller's
            // first read is not awaited by the start (RegistryPoller.ExecuteAsync yields first), so on a
            // loaded machine it can land after the harness's next write. Seen in the check run of #345.
            if (cell)
            {
                foreach (ServiceDescriptor poller in services
                    .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType == typeof(RegistryPoller))
                    .ToList())
                {
                    services.Remove(poller);
                }
            }

            services.RemoveAll<IObjectStore>();
            services.AddSingleton<IObjectStore>(store);
            services.RemoveAll<IClientSecretSource>();
            services.AddSingleton<IClientSecretSource>(secrets);
            if (options.Clock is { } clock)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
            }

            if (instance is not null)
            {
                // The cell's shared parts, replacing what the entry points registered with TryAdd: one
                // session store and one lease table for every host, as every task reads the one table
                // (plan rows P2-1 and P2-3), and the instance id /readyz names (change request P2-5,
                // decision 2: the harness injects a deterministic id through test services).
                services.RemoveAll<ISessionStore>();
                services.AddSingleton<ISessionStore>(sessionStore!);
                services.RemoveAll<IRefreshLeases>();
                services.AddSingleton(leases!);
                services.RemoveAll<GatewayInstance>();
                services.AddSingleton(new GatewayInstance(instance));
            }

            options.ConfigureServices?.Invoke(services);

            // Beside the console logger, rendering with the formatter the gateway configured for it by
            // name. Last, as a stage host registers its capture, so a test's hook that clears the
            // providers cannot take the capture with them.
            services.AddSingleton<ILoggerProvider>(provider => log.ProviderFor(provider));
        }

        GatewayHarness harness = new(
            options, idp, mockApi, echo, echoListener, store, secrets, log,
            new JsonObject { ["schemaVersion"] = 1, ["revision"] = 0, ["apps"] = new JsonArray() });
        harness.upstreamsByApi = upstreams;
        harness.newHost = NewHost;
        harness.SessionStore = sessionStore;
        harness.RefreshLeases = leases;

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

            for (int i = 0; i < options.Hosts; i++)
            {
                await harness.StartHostCoreAsync(ct);
            }
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }

        return harness;
    }

    /// <summary>
    /// Starts one more gateway host over the same bucket, secrets, provider, session store, lease table
    /// and, under <see cref="KeyRingSharing.OneParameterStore"/>, the same ring: a replacement task
    /// starting in the cell (#332, F2). It reads the registry when it starts, as a task does, and is then
    /// polled only when a test says so.
    /// </summary>
    public Task<GatewayHost> StartHostAsync(CancellationToken ct = default) =>
        Options.KeyRing == KeyRingSharing.FrameworkDefault
            ? throw new InvalidOperationException(
                "a second host needs a key ring the harness sets (GatewayHarnessOptions.KeyRing); the framework's default ring is per machine.")
            : StartHostCoreAsync(ct);

    private async Task<GatewayHost> StartHostCoreAsync(CancellationToken ct)
    {
        if (hosts.Count >= Math.Max(1, Options.InstanceIds.Count) && Options.KeyRing != KeyRingSharing.FrameworkDefault)
        {
            throw new InvalidOperationException($"the harness has instance ids for {Options.InstanceIds.Count} hosts.");
        }

        (GatewayFactory factory, string? instance, ParameterStoreStub? parameterStore) = newHost(hosts.Count);
        GatewayHost host = new(instance, factory, parameterStore);
        hosts.Add(host);
        host.StartServer();

        // In a one-host harness the poller's first read may still be in flight. One more poll waits for it
        // (the source runs one poll at a time) and settles the outcome either way. A cell-mode host has no
        // poller (ConfigureHost), so this is its first read of the registry, as a starting task's is.
        await host.RefreshRegistryAsync(ct);
        long expected = long.Parse(document["revision"]!.ToJsonString(), CultureInfo.InvariantCulture);
        if (host.Registry.Current.Revision != expected)
        {
            throw new InvalidOperationException(
                $"registry revision {expected} did not go live on {host}. The gateway log says why:" + Environment.NewLine
                + string.Join(Environment.NewLine, Log.Rendered.TakeLast(20)));
        }

        return host;
    }

    /// <summary>
    /// A browser whose network reaches the gateway through the stand-in load balancer. With one host every
    /// request goes to it; with more, the balancer alternates between them as the ALB spreads requests over
    /// its healthy targets.
    /// </summary>
    public TestBrowser CreateBrowser() => new(CreateLoadBalancer());

    /// <summary>
    /// A browser whose every request to a gateway name goes to the host <paramref name="route"/> names at
    /// the moment it is sent, so a test can start a login on one host and finish it on another (#345).
    /// </summary>
    public TestBrowser CreateBrowser(Func<GatewayHost> route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return new(new LoadBalancerTransport(() => route().Address));
    }

    /// <summary>
    /// Puts the stub where a Parameter Store client registered before <c>Program.cs</c> would be: under the
    /// ring. <c>AddPlatformHosting</c> moves the client <c>AddPlatformRegistry</c> registered to a keyed
    /// registration of its own and puts its shield in the unkeyed place (<c>KeyRing.DecorateClient</c>,
    /// #216). A test service runs after <c>Program.cs</c> ("The test app's <c>builder.ConfigureServices</c>
    /// callback is executed after the app's <c>Program.cs</c> code is executed",
    /// https://learn.microsoft.com/aspnet/core/test/integration-tests#customize-webapplicationfactory),
    /// so a plain registration would be handed out bare, past the shield.
    /// Replacing the keyed registration keeps the shield over the stub, as in a cell. Fails loudly when the
    /// ring registered no keyed client, so a change to that shape is seen here rather than passing.
    /// </summary>
    private static void StubUnderTheRing(IServiceCollection services, ParameterStoreStub stub)
    {
        Type type = typeof(IAmazonSimpleSystemsManagement);
        ServiceDescriptor[] inner = [.. services.Where(d => d.ServiceType == type && d.IsKeyedService)];
        if (inner.Length != 1)
        {
            throw new InvalidOperationException(
                $"AddPlatformHosting registered {inner.Length} keyed Parameter Store clients for the key ring, not one; "
                + "the harness puts its stub under the ring there.");
        }

        services.Remove(inner[0]);
        services.Add(new ServiceDescriptor(type, inner[0].ServiceKey, stub));
    }

    /// <summary>The stand-in load balancer on its own, alternating between the hosts in the order they started.</summary>
    public LoadBalancerTransport CreateLoadBalancer() =>
        new(() => hosts[(int)((uint)Interlocked.Increment(ref nextTarget) % (uint)hosts.Count)].Address);

    /// <summary>
    /// Signs <paramref name="browser"/> in to an app the way a user does: a navigation to <c>/bff/login</c>,
    /// through the test provider (which approves at once, as <see cref="TestIdentityProvider.DefaultSubject"/>)
    /// and the app's callback, back to the shell. Returns the shell the browser landed on. Throws, naming
    /// where the login ended, when it did not land on the app's own origin with a 200.
    /// </summary>
    public async Task<BrowserResponse> SignInAsync(TestBrowser browser, string appId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(browser);
        HarnessApp app = App(appId);
        BrowserResponse landed = await browser.NavigateAsync(app.Url("/bff/login"), ct: ct);
        if (landed.StatusCode != System.Net.HttpStatusCode.OK || !FetchMetadata.IsSameOrigin(app.Origin, landed.Url)
            || landed.RedirectedFrom.All(hop => hop.AbsolutePath != "/signin-oidc"))
        {
            throw new InvalidOperationException($"signing in to {app} ended at {landed}, not at the app's shell after its callback.");
        }

        return landed;
    }

    /// <summary>A browser signed in to every app of the first registry revision, in the order <see cref="GatewayHarnessOptions.Apps"/> lists them.</summary>
    public async Task<TestBrowser> CreateSignedInBrowserAsync(CancellationToken ct = default)
    {
        TestBrowser browser = CreateBrowser();
        try
        {
            foreach (string appId in Options.Apps)
            {
                await SignInAsync(browser, appId, ct);
            }
        }
        catch
        {
            browser.Dispose();
            throw;
        }

        return browser;
    }

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

    /// <summary>
    /// Writes <paramref name="next"/> as the registry object, as it stands, and polls once. The outcome is
    /// the write's: <see cref="RefreshOutcome.Swapped"/> when the written document is live and
    /// <see cref="RefreshOutcome.Rejected"/> when it was rejected, whichever poll read it first.
    /// </summary>
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
        string written;
        await registryWrites.WaitAsync(ct);
        try
        {
            string before = await RegistryETagAsync(ct);
            if (!await Store.PutAsync(RegistryKey, Utf8(text), before, ct))
            {
                throw new InvalidOperationException("the registry object changed under the harness.");
            }

            // Only the harness writes the object, and only under this lock, so this is the write's ETag.
            written = await RegistryETagAsync(ct);
        }
        finally
        {
            registryWrites.Release();
        }

        return OutcomeOfWrite(await RefreshRegistryAsync(ct), written);
    }

    /// <summary>
    /// The outcome of the write whose ETag is <paramref name="written"/>, from the outcome of the one poll
    /// the harness ran after it (task 6.1a). The gateway's own poller runs too, and its tick can land
    /// between the write and that poll: it swaps the document in, and the harness's conditional read then
    /// answers 304. So a poll that found the object unchanged reports <see cref="RefreshOutcome.Swapped"/>
    /// when the live snapshot was built from this write, and <see cref="RefreshOutcome.Rejected"/> when it
    /// was not: the source answers 304 only for the ETag it settled, and it settles an ETag that is not
    /// live only by rejecting it (<c>RegistrySource.RefreshOnceAsync</c>). Every other outcome is this
    /// poll's own, as it stands.
    /// </summary>
    private RefreshOutcome OutcomeOfWrite(RefreshOutcome polled, string written) =>
        polled != RefreshOutcome.NotModified ? polled
        : string.Equals(Registry.Current.ETag, written, StringComparison.Ordinal) ? RefreshOutcome.Swapped
        : RefreshOutcome.Rejected;

    private async Task<string> RegistryETagAsync(CancellationToken ct)
    {
        StoredObject current = await Store.GetAsync(RegistryKey, ifNoneMatch: null, ct)
            ?? throw new InvalidOperationException("the registry object is missing.");
        current.Body?.Dispose();
        return current.ETag;
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
    /// Rotates an app onto a new client while the gateway runs (#332, F7 and F9): a new client at the
    /// provider with its own secret where the gateway reads it, and the app's entry naming it in a new
    /// registry revision. The old client stays registered at the provider, as it does until an operator
    /// deletes it, so what refuses a login under it is the gateway and not the provider's client list.
    /// <see cref="App"/> names the new client afterwards. Polls the first host only, like every write.
    /// </summary>
    public async Task<(HarnessApp Before, HarnessApp After, RefreshOutcome Outcome)> RotateClientAsync(string appId, CancellationToken ct = default)
    {
        HarnessApp before = App(appId);
        int rotation = rotations.TryGetValue(appId, out int done) ? done + 1 : 1;
        rotations[appId] = rotation;

        HarnessApp after = before with
        {
            ClientId = ClientIdFor(appId) + "-r" + rotation.ToString(CultureInfo.InvariantCulture),
            ClientSecret = "secret-" + appId + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            SecretRef = $"ssm:/spa-platform/harness/apps/{appId}/client-secret:{rotation + 1}",
        };
        Idp.AddClient(
            after.ClientId,
            after.ClientSecret,
            after.Url("/signin-oidc").ToString(),
            [after.Url("/signout-callback-oidc").ToString(), after.Url("/").ToString()]);
        Secrets[after.SecretRef] = after.ClientSecret;
        apps[appId] = after;

        RefreshOutcome outcome = await UpdateRegistryAsync(next =>
        {
            JsonArray entries = next["apps"]!.AsArray();
            int index = entries.Select((entry, i) => (entry, i))
                .Single(pair => pair.entry?["appId"]?.GetValue<string>() == appId).i;
            entries[index] = EntryFor(after, entries[index]!["release"]!.GetValue<string>());
        }, ct);
        return (before, after, outcome);
    }

    private readonly Dictionary<string, int> rotations = new(StringComparer.Ordinal);

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
        foreach (GatewayHost host in hosts)
        {
            await host.DisposeAsync();
        }

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

        Idp.AddClient(
            app.ClientId,
            app.ClientSecret,
            app.Url("/signin-oidc").ToString(),
            // The standard profile returns to the signout callback; the Cognito profile lands on / (ADR-0011).
            [app.Url("/signout-callback-oidc").ToString(), app.Url("/").ToString()]);
        Secrets[app.SecretRef] = app.ClientSecret;

        apps[appId] = app;
        return app;
    }

    /// <summary>The client id the provider issues an app. Fixed per appId, so the mock API can admit it before it exists.</summary>
    public static string ClientIdFor(string appId) => "client-" + appId;

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
