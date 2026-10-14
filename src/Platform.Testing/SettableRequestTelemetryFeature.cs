using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// An <see cref="IRequestTelemetryFeature"/> a test installs on the request, standing in for stage 2.
///
/// Every workstream whose stage can reject a request needs this: the behaviour to prove is "this
/// stage set this rule id", and a stage's own test project has no stage 2 to set the feature up. Both
/// properties start null, which is what stage 2 hands the pipeline.
/// </summary>
public sealed class SettableRequestTelemetryFeature : IRequestTelemetryFeature
{
    public string? Rule { get; set; }

    public string? SessionRef { get; set; }
}
