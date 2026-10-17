using Microsoft.Extensions.DependencyInjection;

namespace Gateway.TestHarness;

/// <summary>How one <see cref="GatewayHarness"/> is set up. The defaults are the two-app hermetic cell.</summary>
public sealed record GatewayHarnessOptions
{
    /// <summary>
    /// The apps in the first registry revision, by fixture appId. Each is built from its definition under
    /// <c>fixtures/apps/</c> and served from <c>fixtures/spa/{appId}</c>. App 3 is left out so F-07 can
    /// onboard it with <see cref="GatewayHarness.OnboardAsync"/>.
    /// </summary>
    public IReadOnlyList<string> Apps { get; init; } = ["app1", "app2"];

    /// <summary>The release every app is published under.</summary>
    public string Release { get; init; } = "2026-10-10.1";

    /// <summary>The issuer name in platform configuration (<c>Issuers:{name}</c>), which every entry names.</summary>
    public string IssuerName { get; init; } = "test-idp";

    /// <summary>
    /// The identity provider profile for the issuer: <c>standard</c> or <c>cognito</c>
    /// (contracts/platform-config.schema.json). <c>cognito</c> also has the test provider advertise
    /// Cognito's logout rather than an <c>end_session_endpoint</c>.
    /// </summary>
    public string Profile { get; init; } = "standard";

    /// <summary>
    /// An API of this name, upstream the echo listener, is added to every app, so the proxy suites can read
    /// what reached an upstream (N-11, N-12). Null adds none.
    /// </summary>
    public string? EchoApi { get; init; } = "echo";

    /// <summary>
    /// <c>ForwardedHeaders:TrustedNetworks</c>. The default trusts the harness's stand-in for the load
    /// balancer, which connects from 127.0.0.1. N-09 sets a network the harness is not in, so the same
    /// headers then come from an untrusted sender.
    /// </summary>
    public IReadOnlyList<string> TrustedNetworks { get; init; } = ["127.0.0.1/32"];

    /// <summary>
    /// The clock for the gateway and the test identity provider, or null for the system clock. The mock
    /// API validates token lifetimes on the system clock, which the harness cannot change, so a fake clock
    /// should start at the real time if API calls must succeed.
    /// </summary>
    public TimeProvider? Clock { get; init; }

    /// <summary>
    /// Whether a session is required, as ADR-0008 requires one before any asset: the shell gate challenges
    /// an asset request without a session, and the proxy's token step takes the session's access token, so
    /// a sessionless call the proxy would forward gets 401 instead (contracts/http-contract.md -> Errors).
    /// True, the default, calls <see cref="GatewayHarness.RequireSession"/>. False leaves the gateway
    /// exactly as <c>Program.cs</c> composes it.
    ///
    /// <para>No composed suite sets it false, so every row runs with the session ADR-0008 requires: a row
    /// about a request without a session asserts the refusal that request gets rather than switching the
    /// gate off (task 6.3c).</para>
    /// </summary>
    public bool SessionRequired { get; init; } = true;

    /// <summary>More gateway configuration, applied over the harness's own. A null value removes nothing: it sets an empty value.</summary>
    public IReadOnlyDictionary<string, string?> Configuration { get; init; } = new Dictionary<string, string?>();

    /// <summary>More service replacements, applied after the harness's own.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }
}
