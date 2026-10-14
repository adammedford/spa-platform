using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Platform.Security;

/// <summary>
/// The request limits Kestrel enforces for the gateway. Kestrel refuses past them itself, with 431
/// and no body for the header limits (<c>contracts/http-contract.md</c> -> Errors; N-20).
///
/// The values are Kestrel's own defaults, written down so that a framework upgrade that moved one
/// would show up as a test failure here rather than as a silent change of the gateway's limits.
/// https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/options#limits
///
/// The ALB in front allows more (64 KB of headers), so these are the binding limits. A session id
/// in one cookie and the fetch-metadata headers are far inside them.
/// </summary>
public static class PlatformSecurityLimits
{
    /// <summary>Request line and headers together, in bytes. Kestrel's default.</summary>
    public const int MaxRequestHeadersTotalSize = 32 * 1024;

    /// <summary>Kestrel's default.</summary>
    public const int MaxRequestHeaderCount = 100;

    /// <summary>The request line alone, in bytes. Kestrel's default.</summary>
    public const int MaxRequestLineSize = 8 * 1024;

    /// <summary>A request body, in bytes: about 28.6 MB. Kestrel's default.</summary>
    public const long MaxRequestBodySize = 30_000_000;

    internal static void Apply(KestrelServerOptions options)
    {
        options.Limits.MaxRequestHeadersTotalSize = MaxRequestHeadersTotalSize;
        options.Limits.MaxRequestHeaderCount = MaxRequestHeaderCount;
        options.Limits.MaxRequestLineSize = MaxRequestLineSize;
        options.Limits.MaxRequestBodySize = MaxRequestBodySize;
    }
}
