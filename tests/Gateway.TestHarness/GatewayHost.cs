using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;
using Platform.Registry;

namespace Gateway.TestHarness;

/// <summary>
/// One gateway host of a <see cref="GatewayHarness"/>: the shipped composition on its own loopback
/// listener, as one ECS task of a cell is (#345). Every host of a harness reads the same bucket, the same
/// client secrets and the same test provider; with <see cref="GatewayHarnessOptions.KeyRing"/> set, they
/// also share the session store, the refresh leases and, under <see cref="KeyRingSharing.OneParameterStore"/>,
/// the key ring.
///
/// Each host polls the registry only when a test says so (<see cref="RefreshRegistryAsync"/>): the
/// background poll interval is an hour, so one host can be left a revision behind another, which is the
/// skew <c>spactl wait --instances</c> exists for (change request P2-5).
/// </summary>
public sealed class GatewayHost
{
    private readonly GatewayFactory factory;

    internal GatewayHost(string? instance, GatewayFactory factory, ParameterStoreStub? parameterStore)
    {
        Instance = instance;
        this.factory = factory;
        ParameterStore = parameterStore;
    }

    /// <summary>
    /// The deterministic instance id the harness gave this host, which its <c>/readyz</c> names
    /// (contracts/pipeline.md, Health endpoints), or null when the harness left the gateway to generate one.
    /// </summary>
    public string? Instance { get; }

    /// <summary>The Parameter Store client this host's key ring reads, or null under the framework's default ring.</summary>
    public ParameterStoreStub? ParameterStore { get; }

    /// <summary>The host's services.</summary>
    public IServiceProvider Services => factory.Services;

    /// <summary>The host's own listener, e.g. <c>http://127.0.0.1:54321/</c>.</summary>
    public Uri Address => factory.Address;

    /// <summary>The host's live registry, as its host resolution reads it.</summary>
    public IRegistry Registry => Services.GetRequiredService<IRegistry>();

    /// <summary>Polls the registry object once on this host only, as its poller would on its next tick.</summary>
    public Task<RefreshOutcome> RefreshRegistryAsync(CancellationToken ct = default) =>
        Services.GetRequiredService<RegistrySource>().RefreshAsync(ct);

    /// <summary>
    /// This host's own <c>/readyz</c>, read straight from its listener: the status, and the
    /// <c>revision</c> and <c>instance</c> a 200 carries.
    /// </summary>
    public async Task<(HttpStatusCode Status, long? Revision, string? Instance)> ReadyzAsync(CancellationToken ct = default)
    {
        using HttpClient http = new(new SocketsHttpHandler { UseProxy = false });
        using HttpResponseMessage response = await http.GetAsync(new Uri(Address, "/readyz"), ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (response.StatusCode, null, null);
        }

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return (
            response.StatusCode,
            body.RootElement.GetProperty("revision").GetInt64(),
            body.RootElement.TryGetProperty("instance", out JsonElement instance) ? instance.GetString() : null);
    }

    public override string ToString() => $"gateway host {Instance ?? "(generated id)"} at {Address}";

    internal void StartServer() => factory.StartServer();

    internal ValueTask DisposeAsync() => factory.DisposeAsync();
}

/// <summary>How the hosts of a <see cref="GatewayHarness"/> hold their data-protection key ring.</summary>
public enum KeyRingSharing
{
    /// <summary>
    /// No <c>DataProtection</c> section: the framework's default ring, and each host's own session store,
    /// exactly as the one-host suites have always run. A harness of this kind has one host.
    /// </summary>
    FrameworkDefault,

    /// <summary>
    /// The real <c>AddPlatformHosting</c> ring over one <see cref="ParameterStoreStub"/> registered in
    /// every host: one ring for the cell (plan row P2-2).
    /// </summary>
    OneParameterStore,

    /// <summary>The same ring with one stub per host: two rings under one application name, the control.</summary>
    ParameterStorePerHost,
}
