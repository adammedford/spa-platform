using Microsoft.Extensions.Configuration;

namespace Platform.Registry.Tests;

/// <summary>
/// The one way these tests load a JSON file into a host's configuration (issue #75).
/// </summary>
internal static class JsonConfiguration
{
    /// <summary>
    /// Adds the JSON file at <paramref name="path"/>, read straight from disk. <c>AddJsonFile</c> given an
    /// absolute path roots a <c>PhysicalFileProvider</c> at its directory, and that provider's default
    /// <c>ExclusionFilters.Sensitive</c> skips dot-prefixed and hidden files
    /// (https://learn.microsoft.com/dotnet/api/microsoft.extensions.fileproviders.physical.exclusionfilters).
    /// macOS flags every file under <c>.claude/worktrees/</c> hidden, so there it threw
    /// <c>FileNotFoundException</c> for a file that was present. A stream has no provider and so no
    /// filter. The bytes are read now, so no file handle outlives the call.
    /// </summary>
    public static IConfigurationBuilder AddJsonWithoutFileProvider(this IConfigurationBuilder configuration, string path) =>
        configuration.AddJsonStream(new MemoryStream(File.ReadAllBytes(path), writable: false));
}
