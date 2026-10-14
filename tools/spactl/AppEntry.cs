using System.Globalization;
using System.Text.Json.Nodes;

namespace Spactl;

/// <summary>
/// An app definition completed into a registry entry (contracts/app-definition.schema.json: "a registry
/// entry without clientId, clientSecretRef and release"). The one place that knows the difference, so
/// a change to the <c>oauth</c> section, which is draft until G1b (contracts-v1), is absorbed here.
///
/// Every property of the definition is carried over in its order, so a property the contract adds later
/// reaches the entry without a change here; the three it lacks are inserted where registry.example.json
/// puts them: <c>release</c> after <c>host</c>, and the two client values after <c>oauth.issuer</c>.
/// </summary>
internal static class AppEntry
{
    /// <param name="definition">A definition <see cref="Platform.Registry.RegistryValidator.ValidateAppDefinition"/> has accepted.</param>
    /// <param name="client">The infrastructure outputs' <c>clients.{appId}</c>, schema-checked.</param>
    public static JsonObject Complete(JsonObject definition, JsonObject client, string release)
    {
        JsonObject entry = [];
        foreach ((string name, JsonNode? value) in definition)
        {
            entry[name] = name == "oauth" ? CompleteOAuth(value!.AsObject(), client) : value?.DeepClone();
            if (name == "host")
            {
                entry["release"] = release;
            }
        }

        return entry;
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
