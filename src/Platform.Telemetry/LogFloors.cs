using Microsoft.Extensions.Logging;

namespace Platform.Telemetry;

/// <summary>
/// The lowest level a framework or library logger may write at, because below it that logger writes
/// something <c>contracts/telemetry.md</c> says is never logged. A category matches its own name and
/// every name under it.
///
/// Each floor is applied twice. <see cref="PlatformTelemetryExtensions.AddPlatformTelemetry"/> adds it
/// as a filter, so a quiet logger costs nothing. <see cref="PlatformJsonFormatter"/> drops anything
/// below it whatever the filters say, because a filter rule is a default that configuration can
/// override: a more specific <c>Logging:LogLevel</c> entry wins over a code rule
/// (https://learn.microsoft.com/aspnet/core/fundamentals/logging/#how-filtering-rules-are-applied).
/// The formatter is the only place every line passes, so the cap holds there.
/// </summary>
internal static class LogFloors
{
    public static readonly IReadOnlyList<(string Category, LogLevel Floor)> All =
    [
        // The OpenID Connect handler logs the callback URL, with the code and state, at Trace and the
        // redirect headers at Debug (ADR-0007 -> Logging rules this forces).
        ("Microsoft.AspNetCore.Authentication", LogLevel.Information),

        // Decided by implementer: the token-management library handles refresh tokens and access
        // tokens (ADR-0005), so it gets the same cap as the handler it sits beside.
        ("Duende.AccessTokenManagement", LogLevel.Information),

        // The framework's default request log: "Request starting" and "Request finished" at
        // Information, each with the query string. UsePlatformTelemetry's line replaces it (contracts/pipeline.md).
        ("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning),

        // Request logging middleware writes headers and query strings by design, if anyone enables it.
        ("Microsoft.AspNetCore.HttpLogging", LogLevel.Warning),

        // YARP logs "Proxying to {destination}" at Information with the query string, and the
        // HttpClient factory logs each request URI. UsePlatformTelemetry's line and the spans cover both.
        ("Yarp.ReverseProxy.Forwarder", LogLevel.Warning),
        ("System.Net.Http.HttpClient", LogLevel.Warning),
    ];

    /// <summary>True when <paramref name="category"/> is held above <paramref name="level"/>.</summary>
    public static bool Drops(string category, LogLevel level)
    {
        foreach ((string prefix, LogLevel floor) in All)
        {
            if (level < floor && IsUnder(category, prefix))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnder(string category, string prefix) =>
        category.StartsWith(prefix, StringComparison.Ordinal)
        && (category.Length == prefix.Length || category[prefix.Length] == '.');
}
