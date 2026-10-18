using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Platform.Abstractions;

namespace Platform.Registry.Tests;

/// <summary>
/// The contract's own example documents, copied next to the tests by the project file. Every failing
/// case is one of these with exactly one rule broken, so the passing case is never a copy of a value
/// that could drift from <c>contracts/</c>.
/// </summary>
internal static class Examples
{
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "examples", name);

    public static string Text(string name) => File.ReadAllText(PathOf(name));

    public static JsonObject Json(string name) =>
        JsonNode.Parse(Text(name))?.AsObject() ?? throw new InvalidOperationException($"{name} parsed to JSON null.");

    /// <summary><c>contracts/registry.example.json</c>, optionally changed.</summary>
    public static JsonObject Registry(Action<JsonObject>? change = null)
    {
        JsonObject document = Json("registry.example.json");
        change?.Invoke(document);
        return document;
    }

    /// <summary>The example registry's single entry, changed.</summary>
    public static JsonObject RegistryEntry(Action<JsonObject> change) =>
        Registry(document => change(Entry(document, 0)));

    public static JsonObject Entry(JsonObject document, int index) => document["apps"]![index]!.AsObject();

    /// <summary>
    /// The example registry with a second entry: the first one copied under another appId, host and
    /// client id, so that it is valid on its own. <paramref name="change"/> then breaks the second.
    /// </summary>
    public static JsonObject RegistryWithSecondEntry(Action<JsonObject>? change = null) =>
        Registry(document =>
        {
            JsonObject second = Entry(document, 0).DeepClone().AsObject();
            second["appId"] = "app2";
            second["host"] = "app2.spa-poc.test:8443";
            second["oauth"]!["clientId"] = "2example23456789";
            second["oauth"]!["clientSecretRef"] = "ssm:/spa-platform/poc/apps/app2/client-secret:1";
            change?.Invoke(second);
            document["apps"]!.AsArray().Add(second);
        });

    /// <summary>
    /// <c>Policy:*</c> and <c>Issuers:*</c> from <c>contracts/platform-config.example.json</c>, as the
    /// validator reads them.
    /// </summary>
    public static PlatformPolicy Policy()
    {
        JsonObject config = Json("platform-config.example.json");
        JsonObject policy = config["Policy"]!.AsObject();
        return new PlatformPolicy(
            AllowedHostSuffixes: [.. policy["AllowedHostSuffixes"]!.AsArray().Select(node => node!.GetValue<string>())],
            AllowedUpstreams: [.. policy["AllowedUpstreams"]!.AsArray().Select(node => node!.GetValue<string>())],
            Issuers: config["Issuers"]!.AsObject().ToImmutableDictionary(
                issuer => issuer.Key,
                issuer => new IssuerDefinition(
                    issuer.Value!["Authority"]!.GetValue<string>(),
                    issuer.Value!["Profile"]!.GetValue<string>(),
                    new Uri(issuer.Value!["BrowserOrigin"]!.GetValue<string>()))));
    }

    public static PlatformPolicy Policy(Func<PlatformPolicy, PlatformPolicy> change) => change(Policy());

    /// <summary>
    /// <c>maximum</c> on <c>apis[].timeoutSeconds</c> in <c>contracts/registry.schema.json</c>: the
    /// longest timeout the proxy applies as written (ADR-0019). Read from the contract, so a test never
    /// carries its own copy of the number.
    /// </summary>
    public static long TimeoutSecondsMaximum() =>
        Json("registry.schema.json")["$defs"]!["api"]!["properties"]!["timeoutSeconds"]!["maximum"]!.GetValue<long>();
}
