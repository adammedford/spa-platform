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
    /// registers no source at all, which the token step fails closed on: 401 to every call.
    /// </summary>
    public bool TokenSource { get; init; } = true;

    /// <summary>
    /// Puts <c>UsePlatformSecurityHeaders</c> in front of the proxy, as the pipeline contract does, so a
    /// test can read the gateway's policy on a proxied response (N-12).
    /// </summary>
    public bool SecurityHeaders { get; init; }
}
