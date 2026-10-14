using System.Collections.Frozen;
using Platform.Abstractions;

namespace Platform.Hosting.Tests;

/// <summary>
/// Snapshots indexed the way the real registry indexes them: <c>ByHost</c> with
/// <see cref="StringComparer.Ordinal"/>, as <c>src/Platform.Registry/RegistryValidation.cs</c> builds
/// it. <c>InMemoryRegistry.SnapshotOf</c> indexes hosts ignoring case, so a test of exact matching
/// built on it could pass because of the comparer rather than because of stage 4.
/// </summary>
internal static class Snapshots
{
    /// <summary>Revision 0 and no apps: what <c>LiveRegistry.NoSnapshot</c> is before the first load.</summary>
    public static RegistrySnapshot None => With(0);

    public static RegistrySnapshot With(long revision, params AppDefinition[] apps) =>
        new(
            revision,
            revision == 0 ? "" : $"\"etag-{revision}\"",
            apps.ToFrozenDictionary(app => app.Host, StringComparer.Ordinal),
            apps.ToFrozenDictionary(app => app.AppId, StringComparer.Ordinal));
}
