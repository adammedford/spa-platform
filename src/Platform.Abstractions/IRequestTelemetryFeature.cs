namespace Platform.Abstractions;

/// <summary>
/// Where a later stage leaves the two request-line fields only it can know, for stage 2 to read once
/// the pipeline has returned. Stage 2 sets it on the request before calling the rest of the pipeline,
/// and any stage after stage 2 writes it. contracts/telemetry.md -> "How `rule` and `session_ref`
/// reach the request line" is the contract; contracts/pipeline.md has the stage order.
///
/// <para><b>Stage 2 does not install this yet.</b> That is the remainder of task 6.4, in
/// src/Platform.Telemetry. Until it lands, every <c>Features.Get&lt;IRequestTelemetryFeature&gt;()</c>
/// returns null and a null-conditional write records nothing, so a stage owner adding one should not
/// expect to see it on a line. A stage's own test installs Platform.Testing's
/// <c>SettableRequestTelemetryFeature</c> and asserts against that instead, which is the behaviour
/// the test id names either way.</para>
///
/// <para>One stage cannot use this at all: the method allowlist answers 405 in stage 1, before this
/// feature exists. contracts/telemetry.md records that as a known gap.</para>
///
/// contracts/telemetry.md puts <c>rule</c> and <c>session_ref</c> on the one request log line, and
/// counts <c>security.rejections</c> by the same rule id. Stage 2 writes that line, so it needs
/// somewhere to read both from, and neither value is its to compute: the stage that rejected the
/// request knows the rule, and only workstream 3 holds the session key. Change request
/// contracts/changes/6.4-request-telemetry-feature.md asked for this; the integrator's decision there
/// accepted it, as the framework's own idiom and the one <see cref="IAppFeature"/> already uses.
///
/// A stage writes it null-conditionally:
/// <c>context.Features.Get&lt;IRequestTelemetryFeature&gt;()?.Rule = "gate.origin";</c>. A stage's own
/// test project runs without stage 2, so a missing feature must cost nothing.
/// Platform.Testing's settable implementation is what a component test installs instead.
///
/// Source: change request 6.4, decided 14 October 2026. Freezes on landing: it carries fields the
/// G1a telemetry contract already required, and it touches nothing in the G1b draft set.
/// </summary>
public interface IRequestTelemetryFeature
{
    /// <summary>
    /// The rule id from contracts/request-gate.md that this request was rejected under, or null when
    /// it was not rejected.
    ///
    /// Written only by the stage that ends the request with a rejection, which is why nothing here
    /// enforces write-once: a rejection ends the pipeline, so at most one stage writes it, and a
    /// setter that threw would turn a logging mistake into a 500.
    ///
    /// One 401 is not a rejection and leaves this null: the one answered when no access token can be
    /// had for a live session, which contracts/request-gate.md has no id for. That is the only such
    /// case. A 401 for a cookie that resolves to no session is <c>auth.session</c>, and an upstream
    /// API's own 4xx is not the gateway's rejection at all.
    /// </summary>
    string? Rule { get; set; }

    /// <summary>
    /// The session's reference, as contracts/telemetry.md defines it: the first 12 hex characters of
    /// a SHA-256 of the session key. Compute it with <see cref="Abstractions.SessionRef.Of"/> rather
    /// than by hand, so that two writers cannot produce two references for one session. Inside a type
    /// implementing this interface, this property hides the class of the same name, so write
    /// <c>Platform.Abstractions.SessionRef.Of(key)</c> there.
    ///
    /// The session key itself is on this contract's never-logged list and never reaches stage 2.
    /// Workstream 3 writes this field, wherever it holds both the key and the request.
    /// </summary>
    string? SessionRef { get; set; }
}
