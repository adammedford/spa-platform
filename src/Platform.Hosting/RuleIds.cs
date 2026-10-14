namespace Platform.Hosting;

/// <summary>
/// The rule ids from <c>contracts/request-gate.md</c> -> Rule ids that this project's stages reject
/// under. One constant per id, so the string a stage logs and the string it leaves on
/// <c>IRequestTelemetryFeature</c> cannot drift apart.
///
/// <para>Local to this project on purpose: <c>Platform.Security</c> rejects under two of the same ids
/// at stage 6 and spells them itself, because check A-03 keeps a workstream project from referencing
/// another one. <c>contracts/request-gate.md</c> is the one definition; these are copies of it, as
/// <see cref="Problems"/> is for the error codes.</para>
/// </summary>
internal static class RuleIds
{
    /// <summary>Stage 4: the request authority matches no registry <c>host</c>.</summary>
    public const string HostUnknown = "host.unknown";

    /// <summary>Stage 10's asset endpoint: the path failed normalisation.</summary>
    public const string PathInvalid = "path.invalid";

    /// <summary>Stage 10's asset endpoint: a reserved path an SPA may not serve.</summary>
    public const string PathReserved = "path.reserved";
}
