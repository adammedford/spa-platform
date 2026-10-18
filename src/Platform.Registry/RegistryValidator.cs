using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Platform.Abstractions;

namespace Platform.Registry;

/// <summary>
/// The one registry validation library of plan row 2.1, which the gateway and <c>spactl</c> both call
/// so they cannot disagree (ADR-0002; docs/plan/01-plan.md, "Registry validation lives in one library").
///
/// Pure: no I/O, no clock, no configuration read. The caller passes the document's text, the
/// policy-bounded part of platform configuration, and the live revision. Two rules in
/// contracts/README.md are deliberately not here, because they need I/O: <c>clientSecretRef</c>
/// resolving (tasks 2.2 and 2.5) and the release prefix existing in S3 (task 2.6).
/// </summary>
public static class RegistryValidator
{
    /// <summary>
    /// A duplicate member name is refused rather than resolved, because the schema engine and the
    /// model below could otherwise each read a different one of the two values
    /// (https://learn.microsoft.com/dotnet/api/system.text.json.jsondocumentoptions.allowduplicateproperties).
    /// </summary>
    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

    /// <summary>
    /// Checks a registry document against <c>contracts/registry.schema.json</c>, then every rule the
    /// schema cannot express, and models it. Any error rejects the document whole.
    /// </summary>
    /// <param name="liveRevision">
    /// The live snapshot's revision, which the document's must exceed; null at a cold start, when there
    /// is no live snapshot. A rollback is the old content under a new revision.
    /// </param>
    public static RegistryValidation Validate(string document, PlatformPolicy policy, long? liveRevision)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(policy);

        if (!TryParse(document, RegistrySchemas.Registry, out JsonObject? root, out ImmutableArray<RegistryIssue> shapeErrors))
        {
            return new RegistryValidation(0, [], shapeErrors, []);
        }

        Findings findings = new();
        if (!JsonInteger.TryRead(root["revision"]!, out long revision))
        {
            findings.Error(RegistryRules.IntegerOutOfRange, "/revision", "Revision is not an integer between 1 and 9223372036854775807.");
        }
        else if (liveRevision is long live && revision <= live)
        {
            findings.Error(RegistryRules.RevisionNotGreater, "/revision",
                $"Revision {revision} is not greater than the live revision {live}.");
        }

        JsonArray apps = root["apps"]!.AsArray();
        Dictionary<string, int> appIds = new(StringComparer.Ordinal);
        Dictionary<string, int> hosts = new(StringComparer.Ordinal);
        Dictionary<string, int> clientIds = new(StringComparer.Ordinal);
        for (int index = 0; index < apps.Count; index++)
        {
            JsonObject entry = apps[index]!.AsObject();
            string at = $"/apps/{index}";

            Unique(findings, appIds, entry["appId"]!.GetValue<string>(), index, RegistryRules.DuplicateAppId, $"{at}/appId", "appId");
            Unique(findings, hosts, entry["host"]!.GetValue<string>(), index, RegistryRules.DuplicateHost, $"{at}/host", "host");
            Unique(findings, clientIds, entry["oauth"]!["clientId"]!.GetValue<string>(), index, RegistryRules.DuplicateClientId, $"{at}/oauth/clientId", "oauth.clientId");

            CheckEntry(findings, entry, at, policy);
        }

        if (findings.Errors.Count > 0)
        {
            return new RegistryValidation(revision, [], findings.Errors.ToImmutable(), findings.Warnings.ToImmutable());
        }

        ImmutableArray<AppDefinition> modelled = [.. apps.Select(entry => Model(entry!.AsObject()))];
        return new RegistryValidation(revision, modelled, [], findings.Warnings.ToImmutable());
    }

    /// <summary>
    /// Checks an app definition (a registry entry without <c>clientId</c>, <c>clientSecretRef</c> and
    /// <c>release</c>) against <c>contracts/app-definition.schema.json</c> and the policy-bounded rules
    /// that apply to one entry. <c>spactl app add</c> (task 2.6) checks a definition before completing it;
    /// cross-entry uniqueness waits until it is part of a registry document.
    /// </summary>
    public static AppDefinitionValidation ValidateAppDefinition(string document, PlatformPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(policy);

        if (!TryParse(document, RegistrySchemas.AppDefinition, out JsonObject? root, out ImmutableArray<RegistryIssue> shapeErrors))
        {
            return new AppDefinitionValidation(shapeErrors, []);
        }

        Findings findings = new();
        CheckEntry(findings, root, "", policy);
        return new AppDefinitionValidation(findings.Errors.ToImmutable(), findings.Warnings.ToImmutable());
    }

    private static bool TryParse(
        string document,
        string schemaFile,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JsonObject? root,
        out ImmutableArray<RegistryIssue> errors)
    {
        root = null;
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(document, Strict);
            errors = RegistrySchemas.Evaluate(schemaFile, parsed.RootElement);
            if (!errors.IsEmpty)
            {
                return false;
            }

            // The schema has established an object; the rules below assume its shape and nothing more.
            root = JsonObject.Create(parsed.RootElement.Clone())!;
            return true;
        }
        catch (JsonException exception)
        {
            // The parser's message names a position and a token kind, not document content.
            errors = [new RegistryIssue(RegistryRules.NotJson, "", $"The document is not valid JSON: {exception.Message}")];
            return false;
        }
    }

    /// <summary>The rules that hold for one entry on its own, in a registry or as an app definition.</summary>
    private static void CheckEntry(Findings findings, JsonObject entry, string at, PlatformPolicy policy)
    {
        string host = entry["host"]!.GetValue<string>();
        if (!policy.AllowedHostSuffixes.Any(suffix => HostPolicy.EndsWithSuffix(host, suffix)))
        {
            findings.Error(RegistryRules.HostSuffixNotAllowed, $"{at}/host",
                $"Host '{host}' does not end with a suffix Policy:AllowedHostSuffixes allows.");
        }

        if (HostPolicy.PortAboveMaximum(host))
        {
            findings.Error(RegistryRules.PortOutOfRange, $"{at}/host", $"Host '{host}' names a port above 65535.");
        }

        string issuer = entry["oauth"]!["issuer"]!.GetValue<string>();
        if (!policy.Issuers.ContainsKey(issuer))
        {
            findings.Error(RegistryRules.UnknownIssuer, $"{at}/oauth/issuer",
                $"Issuer '{issuer}' is not configured under Issuers:{{name}}.");
        }

        Exact(findings, entry["session"]!["lifetimeMinutes"]!, $"{at}/session/lifetimeMinutes", "lifetimeMinutes", "minutes");

        if (entry["apis"] is not JsonArray apis)
        {
            return;
        }

        HashSet<string> allowed = [.. policy.AllowedUpstreams.Select(HostPolicy.Origin).OfType<string>()];
        Dictionary<string, int> names = new(StringComparer.Ordinal);
        SortedSet<string> origins = new(StringComparer.Ordinal);
        for (int index = 0; index < apis.Count; index++)
        {
            JsonObject api = apis[index]!.AsObject();
            string apiAt = $"{at}/apis/{index}";
            Unique(findings, names, api["name"]!.GetValue<string>(), index, RegistryRules.DuplicateApiName, $"{apiAt}/name", "apis[].name", $"{at}/apis");
            Exact(findings, api["timeoutSeconds"]!, $"{apiAt}/timeoutSeconds", "timeoutSeconds", "seconds");

            string upstream = api["upstream"]!.GetValue<string>();
            if (HostPolicy.PortAboveMaximum(upstream[(upstream.IndexOf("://", StringComparison.Ordinal) + 3)..]))
            {
                findings.Error(RegistryRules.PortOutOfRange, $"{apiAt}/upstream", $"Upstream '{upstream}' names a port above 65535.");
                continue;
            }

            string? origin = HostPolicy.Origin(upstream);
            if (origin is null || !allowed.Contains(origin))
            {
                findings.Error(RegistryRules.UpstreamNotAllowed, $"{apiAt}/upstream",
                    $"Upstream '{upstream}' is not an origin Policy:AllowedUpstreams allows.");
                continue;
            }

            origins.Add(origin);
        }

        if (origins.Count > 1)
        {
            findings.Warning(RegistryRules.MultipleUpstreamOrigins, $"{at}/apis",
                $"The entry names {origins.Count} upstream origins ({string.Join(", ", origins)}). Phase 1 sends one " +
                "access token to each, which is safe only inside one trust domain (contracts/upstream-contract.md).");
        }
    }

    /// <summary>
    /// A schema-valid integer that <see cref="Model"/> reads with <see cref="JsonInteger.Read"/>, which
    /// throws on anything <see cref="JsonInteger.TryRead"/> refuses. The schema's bounds keep both values
    /// inside a <see cref="long"/>, but not every value the engine accepts as an integer is a whole
    /// number: JsonSchema.Net 8.0.5 passes <c>30.0000000000000001</c> as <c>"type": "integer"</c>, and
    /// its exact value has a fractional part (RegistryValidatorTests, the "cannot hold" cases). That is
    /// contracts/README.md's row "A schema-valid integer the platform cannot hold or apply", so it is
    /// reported rather than thrown out of <see cref="Validate"/>, and an app definition with it is refused
    /// before <c>spactl</c> completes it into a registry entry.
    /// </summary>
    private static void Exact(Findings findings, JsonNode value, string path, string property, string unit)
    {
        if (!JsonInteger.TryRead(value, out _))
        {
            findings.Error(RegistryRules.IntegerOutOfRange, path,
                $"{property} is not a whole number of {unit} the platform can hold exactly.");
        }
    }

    private static void Unique(
        Findings findings,
        Dictionary<string, int> seen,
        string value,
        int index,
        string rule,
        string path,
        string property,
        string collection = "/apps")
    {
        if (!seen.TryAdd(value, index))
        {
            findings.Error(rule, path, $"{property} '{value}' is already claimed by {collection}/{seen[value]}.");
        }
    }

    /// <summary>The typed record, from an entry every rule above has accepted.</summary>
    private static AppDefinition Model(JsonObject entry)
    {
        JsonObject oauth = entry["oauth"]!.AsObject();
        return new AppDefinition(
            AppId: entry["appId"]!.GetValue<string>(),
            Host: entry["host"]!.GetValue<string>(),
            Release: entry["release"]!.GetValue<string>(),
            OAuth: new OAuthDefinition(
                Issuer: oauth["issuer"]!.GetValue<string>(),
                ClientId: oauth["clientId"]!.GetValue<string>(),
                ClientSecretRef: oauth["clientSecretRef"]!.GetValue<string>(),
                Scopes: Strings(oauth["scopes"]!.AsArray()),
                Resource: oauth["resource"]?.GetValue<string>()),
            // Absent apis: an app with no upstream (contracts/README.md, "Using the schemas"). The schema's
            // maximum on timeoutSeconds, the proxy's ceiling (ADR-0019), is far inside what a TimeSpan holds.
            Apis: entry["apis"] is JsonArray apis
                ? [.. apis.Select(api => new ApiDefinition(
                    api!["name"]!.GetValue<string>(),
                    new Uri(api["upstream"]!.GetValue<string>(), UriKind.Absolute),
                    TimeSpan.FromSeconds(JsonInteger.Read(api["timeoutSeconds"]!))))]
                : [],
            // Absent csp: an app that adds nothing to the baseline content security policy.
            Csp: entry["csp"] is JsonObject csp
                ? csp.ToImmutableDictionary(directive => directive.Key, directive => Strings(directive.Value!.AsArray()), StringComparer.Ordinal)
                : ImmutableDictionary<string, ImmutableArray<string>>.Empty,
            SessionLifetime: TimeSpan.FromMinutes(JsonInteger.Read(entry["session"]!["lifetimeMinutes"]!)),
            Fingerprint: EntryFingerprint.Of(entry));
    }

    private static ImmutableArray<string> Strings(JsonArray values) => [.. values.Select(value => value!.GetValue<string>())];

    private sealed class Findings
    {
        public ImmutableArray<RegistryIssue>.Builder Errors { get; } = ImmutableArray.CreateBuilder<RegistryIssue>();

        public ImmutableArray<RegistryIssue>.Builder Warnings { get; } = ImmutableArray.CreateBuilder<RegistryIssue>();

        public void Error(string rule, string path, string message) => Errors.Add(new RegistryIssue(rule, path, message));

        public void Warning(string rule, string path, string message) => Warnings.Add(new RegistryIssue(rule, path, message));
    }
}
