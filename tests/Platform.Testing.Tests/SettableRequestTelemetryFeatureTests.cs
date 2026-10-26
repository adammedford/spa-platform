using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The double every workstream whose stage can reject a request installs in place of <c>UsePlatformTelemetry</c>.
/// </summary>
public sealed class SettableRequestTelemetryFeatureTests
{
    [Fact]
    public void A_fresh_feature_carries_no_field()
    {
        // What <c>UsePlatformTelemetry</c> hands the pipeline: a request that was not rejected has no rule, a
        // request with no session has no reference, and a request whose reads were not refused has
        // no refusal.
        // Mutation (#427): initialise ReadRefused to "asset", and this fails.
        SettableRequestTelemetryFeature feature = new();

        Assert.Null(feature.Rule);
        Assert.Null(feature.SessionRef);
        Assert.Null(feature.ReadRefused);
    }

    [Fact]
    public void A_stage_writes_it_through_the_interface_as_the_pipeline_does()
    {
        // The stages reach it as IRequestTelemetryFeature off HttpContext.Features, never as this
        // type, so that is how the double is exercised here.
        IRequestTelemetryFeature feature = new SettableRequestTelemetryFeature();

        feature.Rule = "gate.origin";
        feature.SessionRef = SessionRef.Of("a-session-key");
        feature.ReadRefused = "shell";

        // Mutation (#427): make the ReadRefused setter discard its value, and this fails.
        Assert.Equal("gate.origin", feature.Rule);
        Assert.Equal(SessionRef.Of("a-session-key"), feature.SessionRef);
        Assert.Equal("shell", feature.ReadRefused);
    }

    [Fact]
    public void The_last_write_wins_because_nothing_enforces_write_once()
    {
        // A rejection ends the pipeline, so at most one stage writes Rule and the question should
        // not arise. It is asserted because the decision on change request 6.4 chose this
        // deliberately: a setter that threw would turn a logging mistake into a 500.
        IRequestTelemetryFeature feature = new SettableRequestTelemetryFeature { Rule = "path.invalid" };

        feature.Rule = "gate.method";

        Assert.Equal("gate.method", feature.Rule);
    }
}
