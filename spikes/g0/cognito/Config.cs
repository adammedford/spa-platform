using System.Text.Json;
using System.Text.RegularExpressions;

namespace Spike;

/// <summary>One app definition from fixtures/apps, as far as the harness needs it.</summary>
public sealed record AppFixture(string AppId, string Host, string IssuerName, string[] Scopes, string? Resource);

/// <summary>The outputs document's client entry: never the secret, only where it lives (contracts/infra-outputs.schema.json).</summary>
public sealed record ClientOutput(string ClientId, string SecretParameter, int SecretVersion);

/// <summary>The identity fields of the infra outputs document (Q3). The rest of the document is not read.</summary>
public sealed record IdentityOutputs(string Region, string Issuer, string CognitoDomain,
    IReadOnlyDictionary<string, ClientOutput> Clients, IReadOnlyDictionary<string, string> PasswordParameters, string Source)
{
    /// <summary>Issuers:{name}:BrowserOrigin: https:// plus the managed-login domain.</summary>
    public string BrowserOrigin => "https://" + CognitoDomain;

    /// <summary>The pool id, the last segment of the issuer URL (https://cognito-idp.{region}.amazonaws.com/{poolId}).</summary>
    public string UserPoolId => Issuer.TrimEnd('/').Split('/')[^1];
}

public static partial class Config
{
    public static readonly string[] AppIds = ["app1", "app2"];

    /// <summary>The repository root: the nearest folder upward with a .git entry (a worktree has a .git file).</summary>
    public static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        throw new InvalidOperationException("not inside the repository: no .git found above the harness");
    }

    /// <summary>The two app definitions under test. A problem is added for each that is missing or unreadable.</summary>
    public static List<AppFixture> LoadApps(string root, List<string> problems)
    {
        var apps = new List<AppFixture>();
        foreach (var id in AppIds)
        {
            var path = Path.Combine(root, "fixtures", "apps", id + ".json");
            if (!File.Exists(path)) { problems.Add($"fixtures/apps/{id}.json is missing"); continue; }
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var r = doc.RootElement;
                var oauth = r.GetProperty("oauth");
                apps.Add(new AppFixture(
                    r.GetProperty("appId").GetString()!,
                    r.GetProperty("host").GetString()!,
                    oauth.GetProperty("issuer").GetString()!,
                    oauth.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()!).ToArray(),
                    oauth.TryGetProperty("resource", out var res) ? res.GetString() : null));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                problems.Add($"fixtures/apps/{id}.json cannot be read: {ex.Message}");
            }
        }
        return apps;
    }

    /// <summary>A path relative to the repository when it is inside it, and as given otherwise.</summary>
    public static string Display(string root, string path)
        => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? Path.GetRelativePath(root, path) : path;

    /// <summary>Where the outputs document is looked for, in order, when --outputs is not given.</summary>
    public static string[] DefaultOutputsPaths(string root) =>
    [
        Path.Combine(root, "infra", "envs", "poc", "outputs.json"),                 // task 1.7 writes it after every apply
        Path.Combine(root, "spikes", "g0", "cognito", "outputs.local.json"),        // outputs-from-terraform.sh writes it
    ];

    /// <summary>
    /// Reads the identity fields of the outputs document and holds each to its rule in contracts/infra-outputs.schema.json.
    /// Only these fields are checked: before the platform root is applied the document cannot carry the rest.
    /// </summary>
    public static IdentityOutputs? LoadOutputs(string path, string user, List<string> problems)
    {
        if (!File.Exists(path)) { problems.Add($"the infra outputs document {path} does not exist"); return null; }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (JsonException ex) { problems.Add($"the infra outputs document {path} is not JSON: {ex.Message}"); return null; }

        using (doc)
        {
            var r = doc.RootElement;
            string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var region = Str(r, "region");
            if (region is null || !Region().IsMatch(region)) problems.Add("outputs: region is missing or not a region name");
            var issuer = Str(r, "issuer");
            if (issuer is null || !issuer.StartsWith("https://", StringComparison.Ordinal) || issuer.Any(char.IsWhiteSpace)) problems.Add("outputs: issuer is missing or not an https URL (task 1.1)");
            var domain = Str(r, "cognitoDomain");
            if (domain is null || !DnsName().IsMatch(domain)) problems.Add("outputs: cognitoDomain is missing or not a host name (task 1.1)");

            var clients = new Dictionary<string, ClientOutput>(StringComparer.Ordinal);
            if (!r.TryGetProperty("clients", out var cs) || cs.ValueKind != JsonValueKind.Object) problems.Add("outputs: clients is missing (task 1.8)");
            else
            {
                foreach (var id in AppIds)
                {
                    if (!cs.TryGetProperty(id, out var c) || c.ValueKind != JsonValueKind.Object) { problems.Add($"outputs: clients.{id} is missing (task 1.8)"); continue; }
                    var extra = c.EnumerateObject().Select(p => p.Name).Except(["clientId", "secretParameter", "secretVersion"]).ToList();
                    if (extra.Count > 0) problems.Add($"outputs: clients.{id} has properties the contract does not allow: {string.Join(", ", extra)}");
                    var clientId = Str(c, "clientId");
                    var parameter = Str(c, "secretParameter");
                    var version = c.TryGetProperty("secretVersion", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
                    if (string.IsNullOrEmpty(clientId)) problems.Add($"outputs: clients.{id}.clientId is missing");
                    if (parameter is null || !ParameterName().IsMatch(parameter)) problems.Add($"outputs: clients.{id}.secretParameter is missing or not a parameter name");
                    if (version < 1) problems.Add($"outputs: clients.{id}.secretVersion is missing or below 1");
                    if (clientId is not null && parameter is not null && version >= 1) clients[id] = new ClientOutput(clientId, parameter, version);
                }
            }

            var passwords = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!r.TryGetProperty("testUsers", out var us) || us.ValueKind != JsonValueKind.Object) problems.Add("outputs: testUsers is missing (task 1.1)");
            else
            {
                foreach (var u in us.EnumerateObject())
                    if (Str(u.Value, "passwordParameter") is { } p && ParameterName().IsMatch(p)) passwords[u.Name] = p;
                if (!passwords.ContainsKey(user)) problems.Add($"outputs: testUsers.{user}.passwordParameter is missing (task 1.1)");
            }

            // Returned whenever the identity root's fields are sound, so the issuer can be checked even while the apps root's are not.
            var identityRootSound = region is not null && Region().IsMatch(region) && issuer is not null && issuer.StartsWith("https://", StringComparison.Ordinal)
                && !issuer.Any(char.IsWhiteSpace) && domain is not null && DnsName().IsMatch(domain);
            return identityRootSound ? new IdentityOutputs(region!, issuer!, domain!, clients, passwords, path) : null;
        }
    }

    [GeneratedRegex(@"^[a-z]{2}(-[a-z]+)+-[1-9][0-9]*$")]
    private static partial Regex Region();

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$")]
    private static partial Regex DnsName();

    [GeneratedRegex(@"^/[A-Za-z0-9][A-Za-z0-9._/-]*$")]
    private static partial Regex ParameterName();
}
