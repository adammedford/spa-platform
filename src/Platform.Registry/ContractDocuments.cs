using System.Collections.Immutable;
using System.Text.Json;

namespace Platform.Registry;

/// <summary>
/// The contract checks <c>spactl</c> (plan row 2.6) makes on documents other than a registry document or
/// an app definition: the infrastructure outputs it completes an app definition from, the platform
/// configuration it reads the policy from, and the <c>appId</c> and <c>release</c> it builds a release
/// prefix out of (contracts/s3-layout.md: "the key is built from registry values ... never from an
/// unchecked path segment").
///
/// Each check evaluates the contract schemas loaded into one registry, as contracts/README.md, "Using
/// the schemas", requires of <c>spactl</c>, so a rule such as the <c>appId</c> pattern exists once.
/// Pure, like <see cref="RegistryValidator"/>: the caller reads the file.
/// </summary>
public static class ContractDocuments
{
    /// <summary>Duplicate member names are refused, for the reason <see cref="RegistryValidator"/> gives.</summary>
    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

    /// <summary><c>infra/envs/poc/outputs.json</c> against <c>contracts/infra-outputs.schema.json</c>.</summary>
    public static ImmutableArray<RegistryIssue> ValidateInfraOutputs(string document) =>
        Evaluate(RegistrySchemas.InfraOutputs, document);

    /// <summary>A platform configuration file against <c>contracts/platform-config.schema.json</c>.</summary>
    public static ImmutableArray<RegistryIssue> ValidatePlatformConfiguration(string document) =>
        Evaluate(RegistrySchemas.PlatformConfiguration, document);

    /// <summary>Whether <paramref name="appId"/> satisfies <c>registry.schema.json#/$defs/appId</c>.</summary>
    public static bool IsAppId(string appId) => Satisfies("appId", appId);

    /// <summary>Whether <paramref name="release"/> satisfies <c>registry.schema.json#/$defs/release</c>.</summary>
    public static bool IsRelease(string release) => Satisfies("release", release);

    private static bool Satisfies(string definition, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return RegistrySchemas.SatisfiesRegistryDefinition(definition, JsonSerializer.SerializeToElement(value));
    }

    private static ImmutableArray<RegistryIssue> Evaluate(string schemaFile, string document)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(document, Strict);
            return RegistrySchemas.Evaluate(schemaFile, parsed.RootElement);
        }
        catch (JsonException exception)
        {
            // The parser's message names a position and a token kind, not document content.
            return [new RegistryIssue(RegistryRules.NotJson, "", $"The document is not valid JSON: {exception.Message}")];
        }
    }
}
