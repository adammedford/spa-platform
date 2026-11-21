namespace Platform.Proxy;

/// <summary>
/// The bound the proxy sets on its upstream connections, on the precedent of
/// <c>Platform.Security.PlatformSecurityLimits</c>: a constant in code rather than a setting or a
/// registry key, because no case has asked for either (#715 Q4). Public, so that a composed suite
/// reads the number rather than copying it (<c>contracts/upstream-contract.md</c> -> Timeouts and
/// failures).
/// </summary>
public static class ProxyLimits
{
    /// <summary>
    /// The most HTTP/1.1 connections one cluster's handler holds open to its one destination, and a
    /// cluster is one app's API (shell or fragment): 64. <see cref="RegistryProxyConfig"/> sets it as
    /// <c>HttpClientConfig.MaxConnectionsPerServer</c> on every cluster it builds. A request that finds
    /// every connection busy waits for one inside the entry's idle limit, so a wait past
    /// <c>timeoutSeconds</c> is 504 <c>upstream_timeout</c> and the upstream never sees it
    /// (<c>ConnectionBoundTests</c>). The name says HTTP/1.1 and per API because that is all it bounds
    /// (#738 Q1).
    ///
    /// <para>
    /// What it bounds is read from the sources, not from the setting's name. YARP copies the setting
    /// to <c>SocketsHttpHandler.MaxConnectionsPerServer</c> in <c>ForwarderHttpClientFactory.ConfigureHandler</c>
    /// (https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Forwarder/ForwarderHttpClientFactory.cs).
    /// The runtime's <c>HttpConnectionPool</c> keeps it as <c>_maxHttp11Connections</c>
    /// (https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/ConnectionPool/HttpConnectionPool.cs),
    /// and the only connection-injection condition that reads it is HTTP/1.1's, in
    /// <c>HttpConnectionPool.Http1.cs</c>. HTTP/2's, in <c>HttpConnectionPool.Http2.cs</c>, consults no
    /// such cap, and YARP sets <c>EnableMultipleHttp2Connections</c> true unless a cluster says
    /// otherwise (the same <c>ConfigureHandler</c>). So an upstream that negotiates HTTP/2 is bounded by
    /// this number neither in connections nor in concurrent requests, until P2-10 measures that case.
    /// A cleartext upstream stays on HTTP/1.1: <c>RequestVersionOrLower</c>, YARP's default policy,
    /// does not attempt h2c.
    /// </para>
    ///
    /// <para>
    /// The unit is one destination pool in one cluster's handler, inside one instance (the glossary's
    /// Instance: in a cell, one task). YARP gives each cluster one handler and keeps it across a
    /// registry swap while the cluster's <c>HttpClientConfig</c> is unchanged (<c>CanReuseOldClient</c>
    /// in the same factory). It is no aggregate: apps that name the same upstream each hold up to this
    /// number to it, and so does each fragment API. Nor is it a lifetime total for an instance: while a
    /// configuration change replaces a cluster's handler, the old handler's connections and the new
    /// one's can be open at once, which this constant does not bound (#738 Q2).
    /// </para>
    ///
    /// <para>
    /// The number is a starting point that nobody has measured. P2-10's load test is where it is
    /// measured against the task's 0.25 vCPU and 1 GB (plan row 1.4).
    /// </para>
    /// </summary>
    public const int MaxHttp11ConnectionsPerApi = 64;
}
