using Microsoft.Extensions.Hosting;

namespace Platform.Hosting;

/// <summary>
/// The host's own bounds on stopping, set by <c>AddPlatformHosting</c>. A constant rather than
/// configuration, as row 4.6's Kestrel limits are in <c>Platform.Security</c> (change request MR-2,
/// "Which part is whose"; #363).
/// </summary>
public static class PlatformHostingLimits
{
    /// <summary>
    /// How long the host waits, once told to stop, for requests in flight before it aborts them
    /// (<c>HostOptions.ShutdownTimeout</c>). 25 seconds, the spec's F1 bound (#332):
    /// above the 21 s worst-case refresh, acquisition included, so a refresh under way when ECS sends
    /// SIGTERM finishes (change request P2-3, the lease's time to live); and below the gateway
    /// container's 30 s <c>stopTimeout</c>, which workstream 1 sets, so the process ends on its own
    /// terms before ECS kills it.
    /// </summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(25);

    /// <summary>
    /// After configuration, so the host's own <c>shutdownTimeoutSeconds</c> key cannot move it.
    /// </summary>
    internal static void Apply(HostOptions options) => options.ShutdownTimeout = ShutdownTimeout;
}
