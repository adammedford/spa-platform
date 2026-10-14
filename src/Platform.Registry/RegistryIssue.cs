namespace Platform.Registry;

/// <summary>
/// One finding against a registry document or an app definition. <paramref name="Path"/> is a JSON
/// pointer into the document (<c>/apps/1/host</c>), empty for the document itself.
///
/// A message names values the registry already publishes (an appId, a host, an upstream origin), never
/// anything read from a secret: the registry carries only <c>clientSecretRef</c>, a reference (ADR-0003).
/// </summary>
/// <param name="Rule">One of <see cref="RegistryRules"/>, so a caller can tell findings apart without parsing text.</param>
public sealed record RegistryIssue(string Rule, string Path, string Message)
{
    public override string ToString() => $"{(Path.Length == 0 ? "/" : Path)}: {Message} [{Rule}]";
}

/// <summary>
/// The rule each <see cref="RegistryIssue"/> reports. Apart from <see cref="Schema"/> and
/// <see cref="NotJson"/>, each is one row of contracts/README.md, "Rules that JSON Schema cannot express".
/// </summary>
public static class RegistryRules
{
    /// <summary>The bytes are not one JSON value, or an object names a property twice.</summary>
    public const string NotJson = "not-json";

    /// <summary>The document does not match its contract schema.</summary>
    public const string Schema = "schema";

    public const string RevisionNotGreater = "revision-not-greater";
    public const string DuplicateAppId = "duplicate-app-id";
    public const string DuplicateHost = "duplicate-host";
    public const string DuplicateClientId = "duplicate-client-id";
    public const string DuplicateApiName = "duplicate-api-name";
    public const string HostSuffixNotAllowed = "host-suffix-not-allowed";
    public const string UpstreamNotAllowed = "upstream-not-allowed";
    public const string UnknownIssuer = "unknown-issuer";
    public const string PortOutOfRange = "port-out-of-range";

    /// <summary>A warning, not an error: upstream-contract.md, "One token per app".</summary>
    public const string MultipleUpstreamOrigins = "multiple-upstream-origins";
}
