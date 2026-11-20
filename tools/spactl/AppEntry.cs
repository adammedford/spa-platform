using System.Globalization;
using System.Text.Json.Nodes;

namespace Spactl;

/// <summary>
/// An app definition completed into a registry entry (contracts/app-definition.schema.json: "a registry
/// entry without clientId, clientSecretRef and release, and with each fragment without clientId,
/// clientSecretRef and release"). The one place that knows the difference, so a change to the
/// <c>oauth</c> section, which is draft until G1b (contracts-v1), is absorbed here.
///
/// Every property of the definition is carried over in its order, so a property the contract adds later
/// reaches the entry without a change here; the three it lacks are inserted where registry.example.json
/// puts them: <c>release</c> after <c>host</c>, and the two client values after <c>oauth.issuer</c>. A
/// fragment is completed the same way, its <c>release</c> after its <c>oauth</c>, as the example puts it.
/// </summary>
internal static class AppEntry
{
    /// <param name="definition">A definition <see cref="Platform.Registry.RegistryValidator.ValidateAppDefinition"/> has accepted.</param>
    /// <param name="client">The infrastructure outputs' <c>clients.{appId}</c>, schema-checked.</param>
    /// <param name="fragmentReleases">
    /// The release of every fragment the definition names, by fragmentId. The caller has checked that
    /// <paramref name="client"/>'s <c>fragments</c> holds a client for each.
    /// </param>
    public static JsonObject Complete(
        JsonObject definition,
        JsonObject client,
        string release,
        IReadOnlyDictionary<string, string> fragmentReleases)
    {
        JsonObject entry = [];
        foreach ((string name, JsonNode? value) in definition)
        {
            entry[name] = name switch
            {
                "oauth" => CompleteOAuth(value!.AsObject(), client),
                "fragments" => new JsonArray([.. value!.AsArray().Select(fragment =>
                    (JsonNode)CompleteFragment(fragment!.AsObject(), client, fragmentReleases))]),
                _ => value?.DeepClone(),
            };
            if (name == "host")
            {
                entry["release"] = release;
            }
        }

        return entry;
    }

    /// <summary>The fragmentIds a definition names, in its order: what the caller resolves releases and clients for.</summary>
    public static IReadOnlyList<string> FragmentIds(JsonObject definition) =>
        definition["fragments"] is JsonArray fragments
            ? [.. fragments.Select(fragment => fragment!["fragmentId"]!.GetValue<string>())]
            : [];

    private static JsonObject CompleteFragment(
        JsonObject fragment,
        JsonObject client,
        IReadOnlyDictionary<string, string> fragmentReleases)
    {
        string fragmentId = fragment["fragmentId"]!.GetValue<string>();
        JsonObject fragmentClient = client["fragments"]![fragmentId]!.AsObject();
        JsonObject completed = [];
        foreach ((string name, JsonNode? value) in fragment)
        {
            completed[name] = name == "oauth" ? CompleteOAuth(value!.AsObject(), fragmentClient) : value?.DeepClone();
            if (name == "oauth")
            {
                completed["release"] = fragmentReleases[fragmentId];
            }
        }

        return completed;
    }

    private static JsonObject CompleteOAuth(JsonObject oauth, JsonObject client)
    {
        JsonObject completed = [];
        foreach ((string name, JsonNode? value) in oauth)
        {
            completed[name] = value?.DeepClone();
            if (name == "issuer")
            {
                completed["clientId"] = client["clientId"]!.DeepClone();
                completed["clientSecretRef"] = SecretReference(client);
            }
        }

        return completed;
    }

    /// <summary>
    /// <c>ssm:{name}:{version}</c> (registry.schema.json#/$defs/clientSecretRef, ADR-0003) from the
    /// outputs' parameter name and version. A reference, never the secret: the outputs hold none.
    /// </summary>
    private static string SecretReference(JsonObject client) =>
        "ssm:" + client["secretParameter"]!.GetValue<string>() + ":"
        + client["secretVersion"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture);
}
