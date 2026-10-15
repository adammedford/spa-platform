using System.Globalization;
using System.Net;
using Platform.Abstractions;
using Platform.Testing;
using Yarp.ReverseProxy.Configuration;

namespace Platform.Proxy.Tests;

/// <summary>
/// Task 4.1a: the largest <c>apis[].timeoutSeconds</c> the proxy actually applies, pinned here so
/// the number lives in CI rather than in a decision document or a comment. It is
/// <b>2,147,483 seconds</b>, 24.855 days, and workstream 2 validates against it in task 2.1b.
///
/// What makes it worth a test is that there is no other signal. Above the ceiling YARP neither
/// throws nor logs: it saturates the idle limit at <see cref="int.MaxValue"/> milliseconds and
/// forwards as normal, so the registry states one idle limit and the proxy applies another with
/// nothing to show for it. The grilling behind this ticket predicted an
/// <c>ArgumentOutOfRangeException</c> out of <c>CancelAfter</c>; the measurement says otherwise,
/// and <see cref="Nothing_objects_above_the_ceiling_which_is_why_it_has_to_be_validated"/> is that
/// correction in test form.
///
/// None of these read <see cref="ProxyTimeout.MaximumTimeoutSeconds"/> to decide where the
/// boundary is. <see cref="YarpActivityTimeout"/> measures it off YARP, and a YARP or .NET upgrade
/// that moves it fails here, by name.
/// </summary>
public sealed class ProxyTimeoutCeilingTests
{
    /// <summary>The validator's bound before task 2.1b: <c>TimeSpan.MaxValue</c> in whole seconds.</summary>
    private const long WhatTheModelCanHoldInSeconds = 922_337_203_685;

    [Fact]
    public void The_proxy_applies_at_most_2_147_483_seconds_and_Platform_Proxy_publishes_that_number()
    {
        long measured = YarpActivityTimeout.LargestSecondsAppliedExactly();

        Assert.True(
            measured == ProxyTimeout.MaximumTimeoutSeconds,
            $"The proxy's timeout ceiling has moved. {nameof(ProxyTimeout)}."
            + $"{nameof(ProxyTimeout.MaximumTimeoutSeconds)} says "
            + $"{Seconds(ProxyTimeout.MaximumTimeoutSeconds)}, measured against "
            + $"{YarpActivityTimeout.MeasuredAgainst}, but this runtime applies at most "
            + $"{Seconds(measured)} exactly. A YARP or .NET upgrade has changed what the proxy can "
            + "apply: update the constant to the measured value, and tell workstream 2, which "
            + "validates apis[].timeoutSeconds against it (task 2.1b).");
    }

    [Fact]
    public void At_2_147_483_seconds_the_proxy_applies_the_idle_limit_the_registry_states()
    {
        TimeSpan stated = TimeSpan.FromSeconds(ProxyTimeout.MaximumTimeoutSeconds);

        TimeSpan carried = ActivityTimeoutCarriedFor(stated);

        Assert.Equal(stated, carried);
        Assert.Equal(2_147_483_000, YarpActivityTimeout.AppliedMilliseconds(carried));
        Assert.Equal(stated, TimeSpan.FromMilliseconds(YarpActivityTimeout.AppliedMilliseconds(carried)));
    }

    [Fact]
    public void One_second_above_2_147_483_the_proxy_applies_a_different_idle_limit_than_the_one_stated()
    {
        TimeSpan stated = TimeSpan.FromSeconds(ProxyTimeout.MaximumTimeoutSeconds + 1);

        TimeSpan carried = ActivityTimeoutCarriedFor(stated);

        // The registry's value reaches YARP intact: the loss is YARP's, not this project's.
        Assert.Equal(stated, carried);

        int applied = YarpActivityTimeout.AppliedMilliseconds(carried);
        Assert.Equal(int.MaxValue, applied);
        Assert.NotEqual(stated, TimeSpan.FromMilliseconds(applied));
    }

    [Fact]
    public void Every_value_up_to_what_the_model_can_hold_saturates_at_the_same_idle_limit()
    {
        // The gap task 2.1b closes: the validator's present bound is about 430,000 times the
        // ceiling, and the whole span above the ceiling collapses onto one value.
        foreach (long seconds in new[]
        {
            ProxyTimeout.MaximumTimeoutSeconds + 1,
            ProxyTimeout.MaximumTimeoutSeconds * 2,
            WhatTheModelCanHoldInSeconds,
        })
        {
            TimeSpan carried = ActivityTimeoutCarriedFor(TimeSpan.FromSeconds(seconds));

            Assert.Equal(int.MaxValue, YarpActivityTimeout.AppliedMilliseconds(carried));
        }
    }

    [Fact]
    public async Task Nothing_objects_above_the_ceiling_which_is_why_it_has_to_be_validated()
    {
        // A real forward with a timeout the proxy cannot apply: 200, no exception, nothing refused.
        // Silence is the defect, so the only place to catch it is the registry validator.
        EchoUpstream orders = new();
        await using EchoUpstreamListener listener = await orders.ListenAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct,
            TestApps.Entry("app1", apis: [
                new ApiDefinition(
                    "orders",
                    listener.BaseAddress,
                    TimeSpan.FromSeconds(ProxyTimeout.MaximumTimeoutSeconds + 1)),
            ]));
        using HttpClient client = new();

        HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/orders/items"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/items", Assert.Single(orders.Requests).Path);
    }

    /// <summary>
    /// The timeout a registry entry of <paramref name="stated"/> seconds hands to YARP, through the
    /// real configuration path rather than a hand-built <c>ForwarderRequestConfig</c>.
    /// </summary>
    private static TimeSpan ActivityTimeoutCarriedFor(TimeSpan stated)
    {
        AppDefinition app = TestApps.Entry(
            "app1", apis: [new ApiDefinition("orders", new Uri("https://orders.internal"), stated)]);

        ClusterConfig cluster = Assert.Single(
            RegistryProxyConfig.From(InMemoryRegistry.SnapshotOf(1, "\"etag\"", [app])).Clusters);

        return cluster.HttpRequest?.ActivityTimeout
            ?? throw new InvalidOperationException(
                $"{nameof(RegistryProxyConfig)} no longer sets ForwarderRequestConfig.ActivityTimeout, "
                + "so there is no ceiling to pin and nothing bounds a silent upstream.");
    }

    private static string Seconds(long seconds) =>
        seconds.ToString("N0", CultureInfo.InvariantCulture) + " seconds";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
