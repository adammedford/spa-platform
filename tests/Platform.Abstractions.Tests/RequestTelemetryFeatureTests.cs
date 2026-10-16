using System.Reflection;
using Platform.Abstractions;

namespace Platform.Abstractions.Tests;

/// <summary>
/// <see cref="IRequestTelemetryFeature"/> exists so that a stage after <c>UsePlatformTelemetry</c> can write a value
/// <c>UsePlatformTelemetry</c> reads back. Both of those are properties of the interface rather than of any
/// implementation, so they are asserted here: a get-only property would compile and would silently
/// make the whole contract useless to the six stages that have to write it.
/// </summary>
public sealed class RequestTelemetryFeatureTests
{
    [Theory]
    [InlineData(nameof(IRequestTelemetryFeature.Rule))]
    [InlineData(nameof(IRequestTelemetryFeature.SessionRef))]
    public void Every_field_a_later_stage_writes_is_settable(string property)
    {
        PropertyInfo? declared = typeof(IRequestTelemetryFeature).GetProperty(property);

        Assert.NotNull(declared);
        Assert.NotNull(declared.GetGetMethod());
        Assert.NotNull(declared.GetSetMethod());
    }

    [Theory]
    [InlineData(nameof(IRequestTelemetryFeature.Rule))]
    [InlineData(nameof(IRequestTelemetryFeature.SessionRef))]
    public void Every_field_is_nullable_because_most_requests_set_neither(string property)
    {
        // contracts/telemetry.md puts both on every request line, and most requests are neither
        // rejected nor authenticated. A non-nullable string would force a sentinel.
        PropertyInfo declared = typeof(IRequestTelemetryFeature).GetProperty(property)!;

        Assert.Equal(
            NullabilityState.Nullable,
            new NullabilityInfoContext().Create(declared).ReadState);
    }

    [Fact]
    public void The_feature_carries_nothing_but_the_two_contracted_fields()
    {
        // A request feature is a shared contract. Growing it is a change request, not a convenience,
        // and this is the test that makes that true rather than customary.
        Assert.Equal<string[]>(
            ["Rule", "SessionRef"],
            [.. typeof(IRequestTelemetryFeature).GetProperties().Select(p => p.Name).Order()]);

        // Only the two properties' accessors, which are IsSpecialName. Anything else is a method
        // someone added to a contract type.
        Assert.Equal<string[]>(
            [],
            [.. typeof(IRequestTelemetryFeature).GetMethods().Where(m => !m.IsSpecialName).Select(m => m.Name).Order()]);
    }
}
