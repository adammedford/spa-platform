namespace Platform.Registry.Tests;

/// <summary>
/// Platform.Registry embeds copies of the contract schemas, because the gateway image's build stage
/// sees only src/ (Platform.Registry.csproj says why). A copy is safe only while it is the contract, so
/// this reads both files (CODING_STANDARDS.md, "A guard reads the thing it guards").
/// </summary>
public sealed class SchemaCopyTests
{
    private static readonly string Output = AppContext.BaseDirectory;

    public static TheoryData<string> Schemas() =>
        ["registry.schema.json", "app-definition.schema.json", "platform-config.schema.json", "infra-outputs.schema.json"];

    [Theory]
    [MemberData(nameof(Schemas))]
    public void The_embedded_copy_is_the_contract_byte_for_byte(string file)
    {
        byte[] contract = File.ReadAllBytes(Path.Combine(Output, "contracts", file));
        byte[] embedded = File.ReadAllBytes(Path.Combine(Output, "embedded", file));

        Assert.True(contract.AsSpan().SequenceEqual(embedded),
            $"src/Platform.Registry/Schemas/{file} differs from contracts/{file}. Copy the contract over it; never edit the copy.");
    }

    [Fact]
    public void Every_contract_schema_has_a_copy()
    {
        string[] contracts = [.. Directory.GetFiles(Path.Combine(Output, "contracts"), "*.schema.json").Select(Path.GetFileName).Order()!];
        string[] copies = [.. Directory.GetFiles(Path.Combine(Output, "embedded"), "*.schema.json").Select(Path.GetFileName).Order()!];

        Assert.Equal(4, contracts.Length);
        Assert.Equal(contracts, copies);
    }
}
