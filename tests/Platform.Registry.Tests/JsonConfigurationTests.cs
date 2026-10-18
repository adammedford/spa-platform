using Microsoft.Extensions.Configuration;

namespace Platform.Registry.Tests;

/// <summary>
/// How these tests load a JSON file into a host's configuration (issue #75). <c>AddJsonFile</c> reads
/// through a <c>PhysicalFileProvider</c> whose default filters exclude dot-prefixed and hidden files, and
/// macOS flags every file under <c>.claude/worktrees/</c> hidden, so in an agent's worktree it threw
/// <c>FileNotFoundException</c> for a file that was there.
/// </summary>
public sealed class JsonConfigurationTests
{
    [Fact]
    public void A_file_the_file_provider_excludes_is_read()
    {
        // A dot-prefixed name is excluded by the provider's default filters on every operating system
        // (ExclusionFilters.Sensitive includes DotPrefixed,
        // https://learn.microsoft.com/dotnet/api/microsoft.extensions.fileproviders.physical.exclusionfilters),
        // so this fails on CI's Linux runner too, not only on a Mac where the file is flagged hidden.
        DirectoryInfo directory = Directory.CreateTempSubdirectory();
        try
        {
            string path = Path.Combine(directory.FullName, ".platform-config.example.json");
            File.Copy(Examples.PathOf("platform-config.example.json"), path);

            // The input exercises the hazard: through the file provider this file is not found.
            Assert.Throws<FileNotFoundException>(() => new ConfigurationBuilder().AddJsonFile(path).Build());

            IConfigurationRoot configuration = new ConfigurationBuilder().AddJsonWithoutFileProvider(path).Build();

            string expected = Examples.Json("platform-config.example.json")["Registry"]!["Bucket"]!.GetValue<string>();
            Assert.Equal(expected, configuration["Registry:Bucket"]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
