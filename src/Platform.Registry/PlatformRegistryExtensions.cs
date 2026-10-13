using Microsoft.Extensions.Hosting;

namespace Platform.Registry;

/// <summary>
/// Workstream 2's registry registration, from the service table in <c>contracts/pipeline.md</c>.
///
/// <b>Stub.</b> Task I-4 wrote this so <c>Program.cs</c> can call the frozen order on the empty
/// skeleton. Tasks 2.1, 2.2 and 2.5 fill it in: the validator, the S3 object store and registry
/// source with its jittered conditional GET, and the Parameter Store secret source. Keep this
/// signature and add no others.
/// </summary>
public static class PlatformRegistryExtensions
{
    public static IHostApplicationBuilder AddPlatformRegistry(this IHostApplicationBuilder builder) => builder;
}
