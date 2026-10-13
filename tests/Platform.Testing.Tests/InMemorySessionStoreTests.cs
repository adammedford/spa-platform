using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The double against the shared <see cref="SessionStoreContract"/>. Task 3.4's store in
/// <c>Platform.Identity</c> subclasses the same suite, and so must the Phase 2 store.
/// </summary>
public sealed class InMemorySessionStoreTests : SessionStoreContract
{
    protected override ISessionStore CreateStore() => new InMemorySessionStore();
}
