using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The names F-06 and F-07 publish their releases under on the cell (<see cref="ContentRelease"/>): a
/// release prefix is written once (ADR-0009), so the name must follow the bytes.
/// </summary>
public sealed class ContentReleaseTests
{
    private static Dictionary<string, byte[]> Files() => new(FixtureFiles.Spa("app1"), StringComparer.Ordinal);

    [Fact]
    public void The_same_files_get_the_same_name_in_any_order()
    {
        Dictionary<string, byte[]> files = Files();

        Assert.Equal(ContentRelease.Name("f06", files), ContentRelease.Name("f06", files.Reverse()));
    }

    [Fact]
    public void One_changed_byte_gets_another_name()
    {
        Dictionary<string, byte[]> files = Files();
        string before = ContentRelease.Name("f06", files);

        byte[] index = [.. files["index.html"]];
        index[^1] ^= 1;
        files["index.html"] = index;

        Assert.NotEqual(before, ContentRelease.Name("f06", files));
    }

    [Theory]
    [InlineData("f06")]
    [InlineData("onboarding")]
    public void The_name_is_a_release_name_the_registry_schema_admits(string prefix)
    {
        JsonNode release = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "contracts", "registry.schema.json")))!["$defs"]!["release"]!;
        string name = ContentRelease.Name(prefix, Files());

        Assert.Matches(new Regex(release["pattern"]!.GetValue<string>()), name);
        Assert.InRange(name.Length, release["minLength"]!.GetValue<int>(), release["maxLength"]!.GetValue<int>());
        Assert.StartsWith(prefix + "-", name, StringComparison.Ordinal);
    }
}
