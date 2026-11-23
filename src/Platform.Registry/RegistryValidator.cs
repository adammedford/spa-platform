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
        Dictionary<string, string> clientIds = new(StringComparer.Ordinal);
        for (int index = 0; index < apps.Count; index++)
        {
            JsonObject entry = apps[index]!.AsObject();
            string at = $"/apps/{index}";

            Unique(findings, appIds, entry["appId"]!.GetValue<string>(), index, RegistryRules.DuplicateAppId, $"{at}/appId", "appId");
            Unique(findings, hosts, entry["host"]!.GetValue<string>(), index, RegistryRules.DuplicateHost, $"{at}/host", "host");
            ClaimClientId(findings, clientIds, entry["oauth"]!, $"{at}/oauth/clientId");

            CheckEntry(findings, entry, at, policy);
        }

        // Every shell's client id is claimed before any fragment's, so a fragment that repeats a shell's
        // id is the one reported, at fragments[i]/oauth/clientId, whichever order the entries are in
        // (#992: a fragment's clientId is unique across every shell and every fragment).
        for (int index = 0; index < apps.Count; index++)
        {
            if (apps[index]!["fragments"] is not JsonArray fragments)
            {
                continue;
            }

            for (int position = 0; position < fragments.Count; position++)
            {
                ClaimClientId(findings, clientIds, fragments[position]!["oauth"]!, $"/apps/{index}/fragments/{position}/oauth/clientId");
            }
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
                $"Host '{host}' does not end with a suffix Policy:AllowedHostSuffixes allows. " +
                "The entry is outside the cell's bounds: a new host suffix is a cell change, made by an operator, not a registry write.");
        }

        if (HostPolicy.PortAboveMaximum(host))
        {
            findings.Error(RegistryRules.PortOutOfRange, $"{at}/host", $"Host '{host}' names a port above 65535.");
        }

        string issuer = entry["oauth"]!["issuer"]!.GetValue<string>();
        if (!policy.Issuers.ContainsKey(issuer))
        {
            findings.Error(RegistryRules.UnknownIssuer, $"{at}/oauth/issuer",
                $"Issuer '{issuer}' is not configured under Issuers:{{name}}. " +
                "The entry is outside the cell's bounds: adding an issuer is widening the bounds, an operator's change, not a registry write.");
        }

        Exact(findings, entry["session"]!["lifetimeMinutes"]!, $"{at}/session/lifetimeMinutes", "lifetimeMinutes", "minutes");

        HashSet<string> allowed = [.. policy.AllowedUpstreams.Select(HostPolicy.Origin).OfType<string>()];
        CheckApis(findings, entry["apis"], at, allowed);

        if (entry["fragments"] is JsonArray fragments)
        {
            CheckFragments(findings, fragments, at, issuer, allowed, policy);
        }
    }

    /// <summary>
    /// The fragment rules one entry holds on its own (#992; the fragments spec, #961): the cap on how
    /// many, <c>fragmentId</c> unique within the entry, one issuer per host, and each fragment's
    /// <c>apis</c> as its own token set. The field rules (<c>fragmentId</c>, <c>entry</c>, no
    /// <c>host</c> or <c>session</c>, the narrower <c>csp</c>) are the schema's, which has already
    /// passed the document.
    /// </summary>
    private static void CheckFragments(Findings findings, JsonArray fragments, string at, string issuer, HashSet<string> allowed, PlatformPolicy policy)
    {
        // Configuration only: the schema sets no maxItems (registry.schema.json, $defs/fragments).
        if (fragments.Count > policy.FragmentsPerShell)
        {
            findings.Error(RegistryRules.FragmentsPerShellExceeded, $"{at}/fragments",
                $"The entry lists {fragments.Count} fragments; Policy:FragmentsPerShell allows {policy.FragmentsPerShell}. " +
                "The entry is outside the cell's bounds: raising the cap is widening the bounds, an operator's change, not a registry write.");
        }

        Dictionary<string, int> ids = new(StringComparer.Ordinal);
        for (int index = 0; index < fragments.Count; index++)
        {
            JsonObject fragment = fragments[index]!.AsObject();
            string fragmentAt = $"{at}/fragments/{index}";

            Unique(findings, ids, fragment["fragmentId"]!.GetValue<string>(), index, RegistryRules.DuplicateFragmentId,
                $"{fragmentAt}/fragmentId", "fragmentId", $"{at}/fragments");

            string fragmentIssuer = fragment["oauth"]!["issuer"]!.GetValue<string>();
            if (!string.Equals(fragmentIssuer, issuer, StringComparison.Ordinal))
            {
                findings.Error(RegistryRules.FragmentIssuerMismatch, $"{fragmentAt}/oauth/issuer",
                    $"Issuer '{fragmentIssuer}' is not the shell's issuer '{issuer}': one issuer per host.");
            }

            CheckApis(findings, fragment["apis"], fragmentAt, allowed);
        }
    }

    /// <summary>
    /// One token set's <c>apis</c>, the shell's or one fragment's: names unique within it, each
    /// timeout exact, each upstream inside policy, and more than one origin a warning. A shell's names
    /// and each fragment's are separate namespaces, so each call counts its own.
    /// </summary>
    private static void CheckApis(Findings findings, JsonNode? node, string at, HashSet<string> allowed)
    {
        if (node is not JsonArray apis)
        {
            return;
        }

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
                    $"Upstream '{upstream}' is not an origin Policy:AllowedUpstreams allows. " +
                    "The entry is outside the cell's bounds: adding an upstream is widening the bounds, an operator's change, not a registry write.");
                continue;
            }

            origins.Add(origin);
        }

        if (origins.Count > 1)
        {
            findings.Warning(RegistryRules.MultipleUpstreamOrigins, $"{at}/apis",
                $"One token set names {origins.Count} upstream origins ({string.Join(", ", origins)}). Phase 1 sends one " +
                "access token to each, which is safe only inside one trust domain (contracts/upstream-contract.md).");
        }
    }

    /// <summary>
    /// Claims an <c>oauth.clientId</c> for the shell or fragment at <paramref name="path"/>, or reports
    /// <see cref="RegistryRules.DuplicateClientId"/> there, naming the path that claimed it first.
    /// </summary>
    private static void ClaimClientId(Findings findings, Dictionary<string, string> claimed, JsonNode oauth, string path)
    {
        string clientId = oauth["clientId"]!.GetValue<string>();
        if (!claimed.TryAdd(clientId, path))
        {
            findings.Error(RegistryRules.DuplicateClientId, path, $"oauth.clientId '{clientId}' is already claimed by {claimed[clientId]}.");
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
    private static AppDefinition Model(JsonObject entry) =>
        new(
            AppId: entry["appId"]!.GetValue<string>(),
            Host: entry["host"]!.GetValue<string>(),
            Release: entry["release"]!.GetValue<string>(),
            OAuth: OAuth(entry["oauth"]!),
            Apis: Apis(entry["apis"]),
            Csp: Csp(entry["csp"]),
            SessionLifetime: TimeSpan.FromMinutes(JsonInteger.Read(entry["session"]!["lifetimeMinutes"]!)),
            // The whole entry, its fragments included: a change to any part of it, a fragment's
            // included, evicts every scheme of the entry together (#992; AppDefinition.Fingerprint).
            Fingerprint: EntryFingerprint.Of(entry))
        {
            // Absent fragments: an entry that composes none, which means what an entry meant before them.
            Fragments = entry["fragments"] is JsonArray fragments
                ? [.. fragments.Select(fragment => new FragmentDefinition(
                    FragmentId: fragment!["fragmentId"]!.GetValue<string>(),
                    OAuth: OAuth(fragment["oauth"]!),
                    Release: fragment["release"]!.GetValue<string>(),
                    Apis: Apis(fragment["apis"]),
                    Csp: Csp(fragment["csp"]),
                    Entry: fragment["entry"]?.GetValue<string>() ?? PlatformPaths.DefaultEntry))]
                : [],
        };

    private static OAuthDefinition OAuth(JsonNode oauth) =>
        new(
            Issuer: oauth["issuer"]!.GetValue<string>(),
            ClientId: oauth["clientId"]!.GetValue<string>(),
            ClientSecretRef: oauth["clientSecretRef"]!.GetValue<string>(),
            Scopes: Strings(oauth["scopes"]!.AsArray()),
            Resource: oauth["resource"]?.GetValue<string>());

    // Absent apis: an app or fragment with no upstream (contracts/README.md, "Using the schemas"). The
    // schema's maximum on timeoutSeconds, the proxy's ceiling (ADR-0019), is far inside what a TimeSpan holds.
    private static ImmutableArray<ApiDefinition> Apis(JsonNode? node) =>
        node is JsonArray apis
            ? [.. apis.Select(api => new ApiDefinition(
                api!["name"]!.GetValue<string>(),
                new Uri(api["upstream"]!.GetValue<string>(), UriKind.Absolute),
                TimeSpan.FromSeconds(JsonInteger.Read(api["timeoutSeconds"]!))))]
            : [];

    // Absent csp: an app or fragment that adds nothing to the baseline content security policy.
    private static ImmutableDictionary<string, ImmutableArray<string>> Csp(JsonNode? node) =>
        node is JsonObject csp
            ? csp.ToImmutableDictionary(directive => directive.Key, directive => Strings(directive.Value!.AsArray()), StringComparer.Ordinal)
            : ImmutableDictionary<string, ImmutableArray<string>>.Empty;

    private static ImmutableArray<string> Strings(JsonArray values) => [.. values.Select(value => value!.GetValue<string>())];

    private sealed class Findings
    {
        public ImmutableArray<RegistryIssue>.Builder Errors { get; } = ImmutableArray.CreateBuilder<RegistryIssue>();

        public ImmutableArray<RegistryIssue>.Builder Warnings { get; } = ImmutableArray.CreateBuilder<RegistryIssue>();

        public void Error(string rule, string path, string message) => Errors.Add(new RegistryIssue(rule, path, message));

        public void Warning(string rule, string path, string message) => Warnings.Add(new RegistryIssue(rule, path, message));
    }
}
