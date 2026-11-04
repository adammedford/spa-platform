using Microsoft.Extensions.Hosting;

namespace Platform.Hosting;

/// <summary>
/// The host's own bounds on stopping, set by <c>AddPlatformHosting</c>. A constant rather than
/// configuration, as row 4.6's Kestrel limits are in <c>Platform.Security</c> (#363).
/// </summary>
public static class PlatformHostingLimits
{
    /// <summary>
    /// How long the host waits, once told to stop, for requests in flight before it aborts them
    /// (<c>HostOptions.ShutdownTimeout</c>). 25 seconds, the spec's F1 bound (#332): above the
    /// refresh holder's 20 s write deadline, timed from its acquire's send (the 21 s lease less 1 s
    /// of skew), so a successful refresh admitted before the host is told to stop writes its rotated
    /// token before the host stops. The durable write is the promise, not the request's completion,
    /// and the bound is the holder's, not the lease's 21 s and not a loser's wait (change request
    /// P2-3, second amendment, decision 2; #332 F1); and below the gateway container's 30 s
    /// <c>stopTimeout</c>, which workstream 1 sets, so the process ends on its own terms before ECS
    /// kills it.
    /// </summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(25);

    /// <summary>
    /// After configuration, so the host's own <c>shutdownTimeoutSeconds</c> key cannot move it. The
    /// host reads that key, or <c>{PREFIX_}SHUTDOWNTIMEOUTSECONDS</c>, into <c>HostOptions</c>
    /// (https://learn.microsoft.com/aspnet/core/fundamentals/host/generic-host#shutdown-timeout).
    /// </summary>
    internal static void Apply(HostOptions options) => options.ShutdownTimeout = ShutdownTimeout;
}
