namespace Platform.Proxy;

/// <summary>
/// The idle limit the proxy can actually apply, as against the one a registry document is free to
/// state. Task 4.1a: the number lives in code and is held there by
/// <c>tests/Platform.Proxy.Tests/ProxyTimeoutCeilingTests.cs</c>, which measures it off YARP
/// instead of reading it from here, so an upgrade that moves it fails CI rather than passing
/// quietly.
///
/// Workstream 2 validates <c>apis[].timeoutSeconds</c> against this (task 2.1b). It has to be
/// validated somewhere, because above the ceiling nothing objects: no exception, no log, and a
/// forward that still answers 200. The registry states one idle limit and the proxy applies
/// another.
/// </summary>
public static class ProxyTimeout
{
    /// <summary>
    /// The largest <c>apis[].timeoutSeconds</c> the proxy applies exactly: 2,147,483 seconds,
    /// 24.855 days. One second more is applied as 2,147,483,647 ms instead.
    ///
    /// <para>
    /// This is the runtime's limit, not the contract's, which is why <c>registry.schema.json</c>
    /// stays unbounded above. <see cref="RegistryProxyConfig"/> passes
    /// <c>ApiDefinition.Timeout</c> to YARP as <c>ForwarderRequestConfig.ActivityTimeout</c>;
    /// YARP's <c>HttpForwarder.SendAsync</c> is the one caller of
    /// <c>ActivityCancellationTokenSource.Rent</c>, which keeps the timeout as
    /// <c>(int)activityTimeout.TotalMilliseconds</c> and hands that to <c>CancelAfter</c>
    /// (https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Utilities/ActivityCancellationTokenSource.cs).
    /// A double-to-int conversion saturates on .NET rather than overflowing or throwing, so every
    /// value above this one becomes <see cref="int.MaxValue"/> milliseconds. Measured on
    /// .NET 10.0.12 with YARP 2.3.0, and re-measured on every test run.
    /// </para>
    /// </summary>
    public const long MaximumTimeoutSeconds = 2_147_483;
}
