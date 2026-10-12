using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The in-memory double against the shared <see cref="ObjectStoreContract"/>. The S3 store in task
/// 2.2 subclasses the same suite, so the two cannot drift.
/// </summary>
public sealed class InMemoryObjectStoreTests : ObjectStoreContract
{
    protected override IObjectStore CreateStore() => new InMemoryObjectStore();
}
