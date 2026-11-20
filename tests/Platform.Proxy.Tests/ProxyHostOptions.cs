using Platform.Abstractions;

namespace Platform.Proxy.Tests;

/// <summary>What a <see cref="ProxyHost"/> runs besides the proxy's own two entry points.</summary>
public sealed record ProxyHostOptions
{
    /// <summary>The issuer origin the security headers put in <c>form-action</c>.</summary>
    public const string BrowserOrigin = "https://login.spa-poc.test";

    /// <summary>The telemetry stage's stand-in. See <see cref="ProxyHost.TelemetryRules"/>.</summary>
    public bool Telemetry { get; init; }

    /// <summary>
    /// Registers <see cref="ProxyHost.Tokens"/> as the <c>IAccessTokenSource</c>. Off is a host that
    /// registers no source at all, which the token step fails closed on: 401 to every call a route
    /// would forward. A name the app does not list still gets <c>UnknownApi</c>'s 404.
    /// </summary>
    public bool TokenSource { get; init; } = true;

    /// <summary>
    /// Puts <c>UsePlatformSecurityHeaders</c> in front of the proxy, as the pipeline contract does, so a
    /// test can read the gateway's policy on a proxied response (N-12).
    /// </summary>
    public bool SecurityHeaders { get; init; }

    /// <summary>
    /// What host resolution's stand-in pins as <see cref="IFragmentFeature"/>, given the app it pinned
    /// and the fragment the path names (null for a path not under <c>/fragments/{seg}/</c>, or one
    /// whose segment the app does not list). Null here pins as the real stage does: the named fragment
    /// when the app lists it, nothing otherwise (contracts/pipeline.md, <c>UsePlatformHostResolution</c>).
    /// A test that needs a pin the real stage cannot produce, none on a fragment's route or another
    /// fragment's, sets its own.
    /// </summary>
    public Func<AppDefinition, FragmentDefinition?, IFragmentFeature?>? FragmentPin { get; init; }

    /// <summary>
    /// The app host resolution's stand-in pins, given the one whose host the request names. Null pins
    /// that one, as the real stage does; a test of the token step's app cross-check pins another.
    /// </summary>
    public Func<AppDefinition, AppDefinition>? AppPin { get; init; }
}
