using System.Security.Cryptography;
using System.Text;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// A release named for its content, as seed.sh names the seed's: a release prefix is written once
/// (ADR-0009) and spactl leaves identical bytes alone, so a second sitting's publish of the same files
/// is a no-op and changed files get a new name rather than a refused overwrite.
/// </summary>
internal static class ContentRelease
{
    /// <summary>
    /// <paramref name="prefix"/>, a hyphen and twelve hex digits of a SHA-256 over every file's path,
    /// length and bytes in path order: within registry.schema.json's release pattern for a short prefix.
    /// </summary>
    public static string Name(string prefix, IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach ((string path, byte[] bytes) in files.OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{path}\0{bytes.Length}\0"));
            hash.AppendData(bytes);
        }

        return prefix + "-" + Convert.ToHexStringLower(hash.GetHashAndReset())[..12];
    }
}
