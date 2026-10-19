namespace Platform.Proxy;

/// <summary>
/// The idle limit the proxy can actually apply, as against the one a registry document states.
/// Task 4.1a: the number lives in code and is held there by
/// <c>tests/Platform.Proxy.Tests/ProxyTimeoutCeilingTests.cs</c>, which measures it off YARP
/// instead of reading it from here, so an upgrade that moves it fails CI rather than passing
/// quietly. It stays in this project, not in <c>Platform.Abstractions</c>, because its truth
/// depends on a YARP version (ADR-0015 decision 2).
///
/// The contract bounds <c>apis[].timeoutSeconds</c> at this ceiling: <c>registry.schema.json</c>
/// states it as the field's <c>maximum</c>, and the registry validator refuses anything above it
/// (ADR-0019 decision 3). It has to be refused somewhere, because above the ceiling nothing
/// objects: no exception, no log, and a forward that still answers 200, with the registry stating
/// one idle limit and the proxy applying another. The contract states the bound and the proxy
/// proves it (ADR-0015 decision 3): the same test reads the schema's maximum and asserts that the
/// proxy applies it as the registry states it.
/// </summary>
public static class ProxyTimeout
{
    /// <summary>
    /// The largest <c>apis[].timeoutSeconds</c> the proxy applies exactly: 2,147,483 seconds,
    /// 24.855 days. One second more is applied as 2,147,483,647 ms instead.
    ///
    /// <para>
    /// This is the runtime's limit. The <c>maximum</c> that <c>registry.schema.json</c> states for
    /// <c>apis[].timeoutSeconds</c> equals it (ADR-0019 decision 3) but does not read it: the
    /// registry validator reads the schema, never this constant, so when the ceiling moves the
    /// schema has to move with it, by a change request. <see cref="RegistryProxyConfig"/> passes
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
