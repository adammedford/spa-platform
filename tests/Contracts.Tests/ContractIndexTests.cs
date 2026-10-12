using System.Text.Json.Nodes;

namespace Contracts.Tests;

/// <summary>
/// The contracts directory itself: every file the index promises, the draft markers that say what
/// G1b may still change, and the written record of each rule a schema cannot express.
/// </summary>
public sealed class ContractIndexTests
{
    [Theory]
    [MemberData(nameof(EveryContractFile))]
    public void Every_file_the_index_promises_exists_and_says_something(string fileName)
    {
        string path = ContractFiles.Path(fileName);
        Assert.True(File.Exists(path), $"contracts/{fileName} is in the index but not on disk.");
        Assert.True(new FileInfo(path).Length > 200, $"contracts/{fileName} is too short to be a contract.");
    }

    public static TheoryData<string> EveryContractFile() => new(ContractFiles.Files);

    [Theory]
    [MemberData(nameof(EveryProseContract))]
    public void Every_written_contract_names_its_gate_and_its_source(string fileName)
    {
        string text = ContractFiles.ReadText(fileName);
        Assert.Contains("Freezes at:", text);
        Assert.Contains("docs/plan/02-contracts.md", text);
    }

    public static TheoryData<string> EveryProseContract() => new(
        "pipeline.md", "http-contract.md", "request-gate.md", "upstream-contract.md", "s3-layout.md", "telemetry.md");

    [Theory]
    [MemberData(nameof(EverySchema))]
    public void Every_schema_is_draft_2020_12_and_has_an_id_under_the_platform_base(string fileName)
    {
        var schema = ContractFiles.SchemaSource(fileName).AsObject();

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema["$schema"]!.GetValue<string>());
        Assert.Equal(
            "https://spa-platform.internal/contracts/" + fileName,
            schema["$id"]!.GetValue<string>());
    }

    public static TheoryData<string> EverySchema() => new(ContractFiles.SchemaFiles);

    [Theory]
    [MemberData(nameof(EverySchema))]
    public void Every_schema_says_which_gate_freezes_it(string fileName)
    {
        var freeze = ContractFiles.SchemaSource(fileName)["x-freeze"]!.AsObject();

        Assert.Equal("G1a", freeze["gate"]!.GetValue<string>());
        Assert.Equal("frozen-at-g1a", freeze["status"]!.GetValue<string>());
    }

    [Fact]
    public void The_oauth_section_of_an_entry_is_marked_draft_until_G1b()
    {
        // Task I-2: "the identity parts are marked draft until G1b". Task I-6 folds in the G0
        // findings and tags contracts-v1. Until then this marker has to be on the subschema.
        AssertDraftUntilG1b(ContractFiles.SchemaSource(ContractFiles.RegistrySchema)["$defs"]!["oauth"]!);
        AssertDraftUntilG1b(ContractFiles.SchemaSource(ContractFiles.AppDefinitionSchema)["properties"]!["oauth"]!);
    }

    [Fact]
    public void The_IIdpProfile_names_are_marked_draft_until_G1b()
    {
        JsonNode profile = ContractFiles.SchemaSource(ContractFiles.PlatformConfigSchema)
            ["properties"]!["Issuers"]!["additionalProperties"]!["properties"]!["Profile"]!;

        AssertDraftUntilG1b(profile);
    }

    [Fact]
    public void The_schemas_that_freeze_whole_at_G1a_carry_no_draft_part()
    {
        Assert.Null(FindDraft(ContractFiles.SchemaSource(ContractFiles.InfraOutputsSchema)));
    }

    [Theory]
    [InlineData("`revision` must be greater than the live one")]
    [InlineData("`appId` unique in the registry")]
    [InlineData("`host` unique in the registry")]
    [InlineData("`oauth.clientId` unique across apps")]
    [InlineData("`apis[].name` unique within the app")]
    [InlineData("allowed suffix")]
    [InlineData("upstream policy")]
    [InlineData("names an issuer in platform configuration")]
    [InlineData("readable parameter")]
    [InlineData("More than one upstream origin")]
    [InlineData("prefix exists in S3")]
    [InlineData("fingerprint")]
    public void Every_rule_a_schema_cannot_express_is_written_down(string rule)
    {
        // contracts/README.md -> "Rules that JSON Schema cannot express". Each one belongs to the
        // one validation library of task 2.1, so the register has to stay complete.
        string readme = ContractFiles.ReadText("README.md");

        Assert.Contains("Rules that JSON Schema cannot express", readme);
        Assert.Contains(rule, readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_index_lists_every_file_in_the_directory()
    {
        IEnumerable<string> onDisk = new DirectoryInfo(ContractFiles.Path(string.Empty))
            .EnumerateFiles()
            .Select(file => file.Name)
            .Where(name => name != ".gitkeep");

        Assert.Equal(ContractFiles.Files.Order(), onDisk.Order());
    }

    private static void AssertDraftUntilG1b(JsonNode subschema)
    {
        var freeze = subschema["x-freeze"]!.AsObject();
        Assert.Equal("G1b", freeze["gate"]!.GetValue<string>());
        Assert.Equal("draft", freeze["status"]!.GetValue<string>());
    }

    private static string? FindDraft(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject objectNode:
                if (objectNode.TryGetPropertyValue("x-freeze", out JsonNode? freeze)
                    && freeze?["status"]?.GetValue<string>() == "draft")
                {
                    return objectNode["title"]?.GetValue<string>() ?? "(untitled)";
                }

                return objectNode.Select(property => FindDraft(property.Value)).FirstOrDefault(found => found is not null);

            case JsonArray arrayNode:
                return arrayNode.Select(FindDraft).FirstOrDefault(found => found is not null);

            default:
                return null;
        }
    }
}
