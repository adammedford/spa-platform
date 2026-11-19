namespace Platform.Abstractions;

/// <summary>
/// The fragment whose namespace this request is under, set as a request feature by host resolution
/// (<c>UsePlatformHostResolution</c>) beside <see cref="IAppFeature"/>, from the same snapshot, for a
/// request under <c>/fragments/{fragmentId}/</c> of a fragment the resolved entry lists. Absent on
/// every other request, an unknown <c>fragmentId</c> included. The access-token step cross-checks it
/// against the route's metadata and fails closed: a fragment route never forwards the shell's token
/// set. contracts/pipeline.md is the contract; the fragments spec (#961) the source. Freezes on
/// landing, as <see cref="IRequestTelemetryFeature"/> did: a new type that changes no existing stage,
/// owner or signature.
/// </summary>
public interface IFragmentFeature
{
    FragmentDefinition Fragment { get; }
}
