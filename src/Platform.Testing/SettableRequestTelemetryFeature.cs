using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// An <see cref="IRequestTelemetryFeature"/> a test installs on the request, standing in for <c>UsePlatformTelemetry</c>.
///
/// Every workstream whose stage can reject a request needs this: the behaviour to prove is "this
/// stage set this rule id", and a stage's own test project has no <c>UsePlatformTelemetry</c> to set the feature up. Both
/// properties start null, which is what <c>UsePlatformTelemetry</c> hands the pipeline.
/// </summary>
public sealed class SettableRequestTelemetryFeature : IRequestTelemetryFeature
{
    public string? Rule { get; set; }

    public string? SessionRef { get; set; }
}
