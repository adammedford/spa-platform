using System.Text.Json.Nodes;

namespace Contracts.Tests;

/// <summary>Accepts and rejects, with the schema file named in the failure message.</summary>
internal static class Schemas
{
    public static void Accepts(string schemaFile, JsonNode document, string because)
    {
        (bool isValid, string report) = ContractFiles.Evaluate(schemaFile, document);
        Assert.True(isValid, $"{schemaFile} should accept {because}, but reported:\n{report}");
    }

    public static void Rejects(string schemaFile, JsonNode document, string because)
    {
        (bool isValid, _) = ContractFiles.Evaluate(schemaFile, document);
        Assert.True(isValid is false, $"{schemaFile} should reject {because}, but accepted it.");
    }

    public static void AcceptsRegistry(JsonNode document, string because) =>
        Accepts(ContractFiles.RegistrySchema, document, because);

    public static void RejectsRegistry(JsonNode document, string because) =>
        Rejects(ContractFiles.RegistrySchema, document, because);

    public static void AcceptsAppDefinition(JsonNode document, string because) =>
        Accepts(ContractFiles.AppDefinitionSchema, document, because);

    public static void RejectsAppDefinition(JsonNode document, string because) =>
        Rejects(ContractFiles.AppDefinitionSchema, document, because);

    public static void AcceptsPlatformConfig(JsonNode document, string because) =>
        Accepts(ContractFiles.PlatformConfigSchema, document, because);

    public static void RejectsPlatformConfig(JsonNode document, string because) =>
        Rejects(ContractFiles.PlatformConfigSchema, document, because);

    public static void AcceptsInfraOutputs(JsonNode document, string because) =>
        Accepts(ContractFiles.InfraOutputsSchema, document, because);

    public static void RejectsInfraOutputs(JsonNode document, string because) =>
        Rejects(ContractFiles.InfraOutputsSchema, document, because);
}
