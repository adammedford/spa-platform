namespace Contracts.Tests;

/// <summary>
/// Task I-2: "JSON schemas that validate the example documents". Every example in
/// <c>contracts/</c> is the document the plan prints, so these are the passing cases for the
/// document as a whole.
/// </summary>
public sealed class ExampleDocumentTests
{
    [Theory]
    [InlineData(ContractFiles.RegistrySchema, ContractFiles.RegistryExample)]
    [InlineData(ContractFiles.AppDefinitionSchema, ContractFiles.AppDefinitionExample)]
    [InlineData(ContractFiles.PlatformConfigSchema, ContractFiles.PlatformConfigExample)]
    [InlineData(ContractFiles.InfraOutputsSchema, ContractFiles.InfraOutputsExample)]
    public void Example_document_validates(string schemaFile, string exampleFile)
    {
        (bool isValid, string report) = ContractFiles.Evaluate(schemaFile, ContractFiles.Example(exampleFile));

        Assert.True(isValid, $"{exampleFile} failed {schemaFile}:\n{report}");
    }

    [Fact]
    public void Registry_example_is_the_app_definition_example_plus_the_three_completed_fields()
    {
        // "An app definition file is an entry without clientId, clientSecretRef and release."
        // spactl app add completes it, so the two examples must describe the same app.
        var entry = ContractFiles.Example(ContractFiles.RegistryExample)["apps"]![0]!.AsObject();
        var definition = ContractFiles.Example(ContractFiles.AppDefinitionExample).AsObject();

        Assert.Equal(
            entry.Select(property => property.Key).Where(name => name != "release").Order(),
            definition.Select(property => property.Key).Order());
        Assert.Equal(entry["appId"]!.GetValue<string>(), definition["appId"]!.GetValue<string>());
        Assert.Equal(entry["host"]!.GetValue<string>(), definition["host"]!.GetValue<string>());
    }
}
