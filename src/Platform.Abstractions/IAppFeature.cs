namespace Platform.Abstractions;

/// <summary>
/// The app and the snapshot this request was resolved against, set as a request feature by host
/// resolution (task 2.3). Everything after host resolution reads this rather than the registry, so
/// one request never straddles a swap. Source: docs/plan/02-contracts.md -> Interfaces. Freezes at
/// G1a.
/// </summary>
public interface IAppFeature
{
    AppDefinition App { get; }

    RegistrySnapshot Snapshot { get; }
}
