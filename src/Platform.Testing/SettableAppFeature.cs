using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// An <see cref="IAppFeature"/> a test sets directly, standing in for host resolution. Prefer
/// <see cref="For(AppDefinition)"/> over the constructor: it builds a snapshot that contains the app,
/// which is the only pairing the real pipeline can produce.
/// </summary>
public sealed class SettableAppFeature(AppDefinition app, RegistrySnapshot snapshot) : IAppFeature
{
    public AppDefinition App { get; set; } = app;

    public RegistrySnapshot Snapshot { get; set; } = snapshot;

    /// <summary>A feature for one app, with a snapshot that resolves it by host and by id.</summary>
    public static SettableAppFeature For(AppDefinition app) =>
        new(app, InMemoryRegistry.SnapshotOf(revision: 1, etag: "\"" + app.Fingerprint + "\"", [app]));
}
