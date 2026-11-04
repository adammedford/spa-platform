using System.Reflection;
using Platform.Abstractions;

namespace Platform.Abstractions.Tests;

/// <summary>
/// <see cref="IRequestTelemetryFeature"/> exists so that a stage after <c>UsePlatformTelemetry</c> can write a value
/// <c>UsePlatformTelemetry</c> reads back. Settable and nullable are properties of the interface rather than of any
/// implementation, so they are asserted here: a get-only property would compile and would silently
/// make the whole contract useless to the six stages that have to write it.
/// </summary>
public sealed class RequestTelemetryFeatureTests
{
    [Theory]
    [InlineData(nameof(IRequestTelemetryFeature.Rule))]
    [InlineData(nameof(IRequestTelemetryFeature.SessionRef))]
    [InlineData(nameof(IRequestTelemetryFeature.ReadRefused))]
    public void Every_field_a_later_stage_writes_is_settable(string property)
    {
        // Mutation (#427): declare ReadRefused get-only on the interface, and its row fails.
        PropertyInfo? declared = typeof(IRequestTelemetryFeature).GetProperty(property);

        Assert.NotNull(declared);
        Assert.NotNull(declared.GetGetMethod());
        Assert.NotNull(declared.GetSetMethod());
    }

    [Theory]
    [InlineData(nameof(IRequestTelemetryFeature.Rule))]
    [InlineData(nameof(IRequestTelemetryFeature.SessionRef))]
    [InlineData(nameof(IRequestTelemetryFeature.ReadRefused))]
    public void Every_field_is_nullable_because_most_requests_set_none(string property)
    {
        // contracts/telemetry.md puts these on the request line, and most requests are neither
        // rejected, authenticated nor refused a read. A non-nullable string would force a sentinel.
        // Mutation (#427): declare ReadRefused under #nullable disable, and its row fails. (A
        // non-nullable string does not build: SettableRequestTelemetryFeature's string? breaks it.)
        PropertyInfo declared = typeof(IRequestTelemetryFeature).GetProperty(property)!;

        Assert.Equal(
            NullabilityState.Nullable,
            new NullabilityInfoContext().Create(declared).ReadState);
    }

    [Fact]
    public void The_feature_carries_nothing_but_the_three_contracted_fields()
    {
        // A request feature is a shared contract. Growing it is a change request, not a convenience,
        // and this is the test that makes that true rather than customary. The third field,
        // ReadRefused, is change request issue-381-object-read-refusal's item 5.
        // Mutation (#427): add a fourth property to the interface and its Testing stand-in, and this
        // fails. Removing ReadRefused does not build: the theories above name it.
        Assert.Equal<string[]>(
            ["ReadRefused", "Rule", "SessionRef"],
            [.. typeof(IRequestTelemetryFeature).GetProperties().Select(p => p.Name).Order()]);

        // Only the three properties' accessors, which are IsSpecialName. Anything else is a method
        // someone added to a contract type.
        Assert.Equal<string[]>(
            [],
            [.. typeof(IRequestTelemetryFeature).GetMethods().Where(m => !m.IsSpecialName).Select(m => m.Name).Order()]);
    }
}
