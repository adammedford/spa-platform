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

    /// <summary>More gateway configuration, applied over the harness's own. A null value removes nothing: it sets an empty value.</summary>
    public IReadOnlyDictionary<string, string?> Configuration { get; init; } = new Dictionary<string, string?>();

    /// <summary>More service replacements, applied after the harness's own.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>
    /// How many gateway hosts start with the harness (#345). More than one needs a
    /// <see cref="KeyRing"/> other than <see cref="KeyRingSharing.FrameworkDefault"/>: two hosts on the
    /// framework's default ring would share or not share it by accident of the machine.
    /// </summary>
    public int Hosts { get; init; } = 1;

    /// <summary>
    /// The key ring. Anything but <see cref="KeyRingSharing.FrameworkDefault"/> also gives every host the
    /// harness's one <see cref="SwitchableSessionStore"/> and one <see cref="RecordingRefreshLeases"/>, and a
    /// deterministic instance id (<see cref="InstanceIds"/>), which is the cell as P2-5 proves it.
    /// </summary>
    public KeyRingSharing KeyRing { get; init; } = KeyRingSharing.FrameworkDefault;

    /// <summary>
    /// When set, with <see cref="KeyRing"/> set, each host gets a refresh-lease table of its own in place of
    /// the cell's one: two tasks sharing no refresh lease, as before plan row P2-3, which is the control of
    /// its fifty-call row (#346). <see cref="GatewayHarness.RefreshLeases"/> is then null.
    /// </summary>
    public bool RefreshLeasesPerHost { get; init; }

    /// <summary>The instance ids the hosts get, in the order they start, when <see cref="KeyRing"/> is set.</summary>
    public IReadOnlyList<string> InstanceIds { get; init; } = ["host-a", "host-b", "host-c", "host-d"];

    /// <summary>Two hosts over one ring, one session store and one lease table: the cell of plan row P2-5.</summary>
    public static GatewayHarnessOptions TwoHosts => new() { Hosts = 2, KeyRing = KeyRingSharing.OneParameterStore };
}
