using Platform.Abstractions;
using Yarp.ReverseProxy.Configuration;

namespace Platform.Proxy;

/// <summary>
/// YARP's configuration, kept equal to the live snapshot. Task 4.1.
///
/// The swap is a reference change and never an edit: the next configuration is built whole, put in
/// place, and only then is the one it replaced signalled. A request already forwarding against the
/// old configuration keeps a consistent view, which is the same guarantee
/// <see cref="IRegistry.Current"/> gives its own readers.
/// </summary>
public sealed class RegistryProxyConfigProvider : IProxyConfigProvider, IDisposable
{
    /// <summary>
    /// Held across the replace and the revoke together. Two swaps in quick succession would
    /// otherwise interleave, publish one configuration and revoke the other, and leave YARP
    /// reloading to a snapshot that is no longer live.
    /// </summary>
    private readonly object swap = new();

    private readonly IDisposable? subscription;
    private volatile RegistryProxyConfig config;

    /// <param name="registry">
    /// The live registry, or null on the skeleton: <c>AddPlatformRegistry</c> is a stub until task
    /// 2.2, and G1a's exit test starts the composed gateway. With no registry there is no snapshot
    /// and so no route, which leaves every <c>/api</c> call an unlisted name. The parameter stops
    /// being nullable when task 2.2 registers an <see cref="IRegistry"/>.
    /// </param>
    public RegistryProxyConfigProvider(IRegistry? registry)
    {
        config = registry is null
            ? RegistryProxyConfig.NoRoutes()
            : RegistryProxyConfig.From(registry.Current);

        // IRegistry raises this after the swap, so Current is already the new snapshot. The argument
        // is used rather than Current so that two swaps in quick succession cannot both publish the
        // later one and leave the earlier reader unsignalled.
        subscription = registry?.OnSwapped((_, next) => Replace(next));
    }

    public IProxyConfig GetConfig() => config;

    public void Dispose() => subscription?.Dispose();

    private void Replace(RegistrySnapshot next)
    {
        lock (swap)
        {
            RegistryProxyConfig replaced = config;
            config = RegistryProxyConfig.From(next);
            replaced.Revoke();
        }
    }
}
