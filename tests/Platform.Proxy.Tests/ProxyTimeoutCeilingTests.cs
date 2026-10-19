using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Platform.Abstractions;
using Platform.Testing;
using Yarp.ReverseProxy.Configuration;

namespace Platform.Proxy.Tests;

/// <summary>
/// Task 4.1a: the largest <c>apis[].timeoutSeconds</c> the proxy actually applies, pinned here so
/// the number lives in CI rather than in a decision document or a comment. It is
/// <b>2,147,483 seconds</b>, 24.855 days.
///
/// Since G2 the contract bounds the field at that number: <c>contracts/registry.schema.json</c>
/// states it as the <c>maximum</c>, and the registry validator refuses anything above it (ADR-0019
/// decision 3). The contract states the bound and the proxy proves it (ADR-0015 decision 3), which
/// is <see cref="At_the_contract_s_maximum_the_proxy_applies_the_idle_limit_the_registry_states"/>:
/// it reads the maximum out of the schema rather than a copy of it, so a contract edit above the
/// ceiling, or an upgrade that lowers the ceiling below the contract, fails here, in the project
/// that owns YARP.
///
/// What makes it worth a test is that there is no other signal. Above the ceiling YARP neither
/// throws nor logs: it saturates the idle limit at <see cref="int.MaxValue"/> milliseconds and
/// forwards as normal, so the registry states one idle limit and the proxy applies another with
/// nothing to show for it. The grilling behind task 4.1a predicted an
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
            + "apply: update the constant to the measured value, and file a change request for the "
            + "maximum contracts/registry.schema.json states for apis[].timeoutSeconds, which is set "
            + "to this ceiling and which the registry validator enforces (ADR-0019 decision 3).");
    }

    [Fact]
    public void At_the_contract_s_maximum_the_proxy_applies_the_idle_limit_the_registry_states()
    {
        long contracted = ContractedMaximumTimeoutSeconds();
        TimeSpan stated = TimeSpan.FromSeconds(contracted);

        TimeSpan carried = ActivityTimeoutCarriedFor(stated);
        int applied = YarpActivityTimeout.AppliedMilliseconds(carried);

        Assert.Equal(stated, carried);
        Assert.True(
            applied == contracted * 1000,
            $"contracts/registry.schema.json lets apis[].timeoutSeconds be as long as {Seconds(contracted)}, "
            + $"but the proxy applies an entry stating that as {Milliseconds(applied)} rather than "
            + $"{Milliseconds(contracted * 1000)}: this runtime applies at most "
            + $"{Seconds(YarpActivityTimeout.LargestSecondsAppliedExactly())} exactly. The registry would "
            + "accept a timeout the proxy silently replaces. Either the contract's maximum has moved above "
            + "the ceiling or a YARP or .NET upgrade has lowered the ceiling below it. The maximum is the "
            + "integrator's, so the fix is a change request under contracts/changes/ or a dependency held "
            + "back, not an edit here (ADR-0015 decision 3, ADR-0019 decision 3).");
    }

    [Fact]
    public void The_contracted_maximum_is_whatever_the_schema_states_and_its_absence_fails_loudly()
    {
        // The mutations the test above cannot see for itself: a reader that returns a copied number, or
        // takes another keyword of the same definition, such as its minimum. The proxy applies one
        // second exactly too, so either would pass there having proved nothing about the contract.
        JsonNode planted = JsonNode.Parse("""
            { "$defs": { "api": { "properties": {
                "timeoutSeconds": { "type": "integer", "minimum": 1, "maximum": 7 } } } } }
            """)!;
        JsonNode unbounded = JsonNode.Parse("""
            { "$defs": { "api": { "properties": {
                "timeoutSeconds": { "type": "integer", "minimum": 1 } } } } }
            """)!;

        Assert.Equal(7, MaximumTimeoutSecondsIn(planted));
        Assert.Contains(
            MaximumPointer,
            Assert.Throws<InvalidOperationException>(() => MaximumTimeoutSecondsIn(unbounded)).Message,
            StringComparison.Ordinal);
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
        // Up to the validator's bound before task 2.1b, about 430,000 times the ceiling, the whole
        // span collapses onto one idle limit: the span the contract's maximum now refuses (ADR-0019).
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

    /// <summary>
    /// Where the contract bounds <c>apis[].timeoutSeconds</c>, as a JSON pointer into
    /// <c>contracts/registry.schema.json</c>: the definition the registry validator refuses above
    /// (ADR-0019 decision 3), so the value proved here is the one a registry document is held to.
    /// The keyword is inclusive, "less than or exactly equal to"
    /// (https://json-schema.org/draft/2020-12/json-schema-validation#section-6.2.2), so the number
    /// there is itself the longest timeout a registry document may state.
    /// </summary>
    private const string MaximumPointer = "/$defs/api/properties/timeoutSeconds/maximum";

    /// <summary>
    /// The <c>maximum</c> that <c>contracts/registry.schema.json</c> states for
    /// <c>apis[].timeoutSeconds</c>, read from the copy the project file puts beside this assembly.
    /// Read rather than copied (CODING_STANDARDS.md, "A guard reads the thing it guards"): a literal
    /// here would go on agreeing with itself after the contract moved.
    /// </summary>
    private static long ContractedMaximumTimeoutSeconds() =>
        MaximumTimeoutSecondsIn(JsonNode.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "registry.schema.json"))));

    /// <summary>
    /// The number at <see cref="MaximumPointer"/> in <paramref name="schema"/>. It throws, naming the
    /// pointer, rather than proving some other number when the schema no longer states one.
    /// </summary>
    private static long MaximumTimeoutSecondsIn(JsonNode? schema)
    {
        JsonNode? node = schema;

        foreach (string segment in MaximumPointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = (node as JsonObject)?[segment];
        }

        return node is JsonValue value && value.TryGetValue(out long maximum)
            ? maximum
            : throw new InvalidOperationException(
                $"contracts/registry.schema.json states no whole number at {MaximumPointer}, so there is "
                + "no contracted timeout ceiling for the proxy to prove it applies. ADR-0019 decision 3 "
                + "put the maximum there; if the contract has moved it, follow it here.");
    }

    private static string Seconds(long seconds) =>
        seconds.ToString("N0", CultureInfo.InvariantCulture) + " seconds";

    private static string Milliseconds(long milliseconds) =>
        milliseconds.ToString("N0", CultureInfo.InvariantCulture) + " ms";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
