using Microsoft.Extensions.Configuration;

namespace Platform.Registry.Tests;

/// <summary>
/// <c>JsonConfiguration.AddJsonWithoutFileProvider</c>, the way these tests load a JSON file into a host's
/// configuration (issue #75), reads a file that <c>AddJsonFile</c>'s file provider excludes.
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
