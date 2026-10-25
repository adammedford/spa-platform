using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The double against the shared <see cref="RefreshLeasesContract"/>. The DynamoDB lease item in
/// <c>Platform.Identity</c> subclasses the same suite (#344).
/// </summary>
public sealed class InMemoryRefreshLeasesTests : RefreshLeasesContract
{
    protected override IRefreshLeases CreateLeases(TimeProvider clock) => new InMemoryRefreshLeases(clock);
}
