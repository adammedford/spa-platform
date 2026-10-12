using System.Text.Json.Nodes;

namespace Contracts.Tests;

/// <summary>
/// Builds a document to evaluate by taking an example from <c>contracts/</c> and changing one thing.
/// Every failing case is therefore the passing case with exactly one rule broken.
/// </summary>
internal static class Documents
{
    public static JsonObject Registry(Action<JsonObject>? change = null)
    {
        var document = ContractFiles.Example(ContractFiles.RegistryExample).AsObject();
        change?.Invoke(document);
        return document;
    }

    /// <summary>The registry document with its single entry changed.</summary>
    public static JsonObject RegistryEntry(Action<JsonObject> change) =>
        Registry(document => change(document["apps"]![0]!.AsObject()));

    /// <summary>The registry document with the <c>oauth</c> section of its single entry changed.</summary>
    public static JsonObject RegistryOAuth(Action<JsonObject> change) =>
        RegistryEntry(entry => change(entry["oauth"]!.AsObject()));

    /// <summary>The registry document with the first <c>apis</c> element changed.</summary>
    public static JsonObject RegistryApi(Action<JsonObject> change) =>
        RegistryEntry(entry => change(entry["apis"]![0]!.AsObject()));

    public static JsonObject AppDefinition(Action<JsonObject>? change = null)
    {
        var document = ContractFiles.Example(ContractFiles.AppDefinitionExample).AsObject();
        change?.Invoke(document);
        return document;
    }

    public static JsonObject PlatformConfig(Action<JsonObject>? change = null)
    {
        var document = ContractFiles.Example(ContractFiles.PlatformConfigExample).AsObject();
        change?.Invoke(document);
        return document;
    }

    public static JsonObject InfraOutputs(Action<JsonObject>? change = null)
    {
        var document = ContractFiles.Example(ContractFiles.InfraOutputsExample).AsObject();
        change?.Invoke(document);
        return document;
    }

    public static JsonArray Strings(params string[] values) =>
        new(values.Select(value => (JsonNode)JsonValue.Create(value)).ToArray());
}
