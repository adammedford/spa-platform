using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The double against the shared <see cref="RefreshLeasesContract"/>, the suite every
/// <see cref="IRefreshLeases"/> passes (change request P2-3 Decision item 6).
/// </summary>
public sealed class InMemoryRefreshLeasesTests : RefreshLeasesContract
{
    protected override IRefreshLeases CreateLeases(TimeProvider clock) => new InMemoryRefreshLeases(clock);
}
