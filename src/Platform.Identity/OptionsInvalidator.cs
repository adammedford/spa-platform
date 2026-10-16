using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The options invalidator (plan row 3.6, GLOSSARY.md): after a registry swap it evicts an app's
/// cached cookie and OpenID Connect options when the entry's <see cref="AppDefinition.Fingerprint"/>
/// changed or the entry went, and purges the app's sessions when the entry went or names a new client.
///
/// The order is the registry's: <see cref="IRegistry.OnSwapped"/> is raised after the swap, so
/// <see cref="IRegistry.Current"/> is already the new snapshot when this runs, and an options build
/// that follows an eviction reads the new entry (swap, then evict). The mechanism is the one the G0
/// spike proved against the real handler (R-C1 to R-C5, ADR-0017), under the frozen types.
///
/// What it leaves alone is as deliberate as what it removes.
/// <list type="bullet">
/// <item>An entry with the same fingerprint keeps its instances, even though the snapshot holds a new
/// record for it (S-D2). Record equality could not decide this: its immutable collections compare by
/// reference (<c>docs/plan/02-contracts.md</c>).</item>
/// <item>A change that keeps the client, a rotated secret included, keeps the sessions (S-D3, R-C2).
/// The cookie's data-protection purpose and the login state's both derive from the scheme name, which
/// does not change, so an existing cookie and a login in flight both survive the rebuild (S-D4, R-C3).</item>
/// <item>A new client purges, and the purge is not the only guard: the ticket store refuses a record
/// bound to another client whether or not the purge has run (<see cref="AppBoundTicketStore"/>, N-22).</item>
/// </list>
///
/// It subscribes when it is built and the options setups take it as a dependency, so it exists before
/// the first options are built and therefore before there is anything to evict or purge. The session
/// store is resolved only for a purge: a gateway that never purges never needs session settings.
/// </summary>
public sealed partial class OptionsInvalidator : IDisposable
{
    private readonly IServiceProvider services;
    private readonly IOptionsMonitorCache<CookieAuthenticationOptions> cookies;
    private readonly IOptionsMonitorCache<OpenIdConnectOptions> oidc;
    private readonly ILogger<OptionsInvalidator> logger;
    private readonly IDisposable subscription;

    public OptionsInvalidator(
        IRegistry registry,
        IServiceProvider services,
        IOptionsMonitorCache<CookieAuthenticationOptions> cookies,
        IOptionsMonitorCache<OpenIdConnectOptions> oidc,
        ILogger<OptionsInvalidator> logger)
    {
        this.services = services;
        this.cookies = cookies;
        this.oidc = oidc;
        this.logger = logger;
        subscription = registry.OnSwapped(Swapped);
    }

    public void Dispose() => subscription.Dispose();

    private void Swapped(RegistrySnapshot old, RegistrySnapshot next)
    {
        foreach (string appId in old.ByAppId.Keys.Union(next.ByAppId.Keys, StringComparer.Ordinal))
        {
            old.ByAppId.TryGetValue(appId, out AppDefinition? before);
            next.ByAppId.TryGetValue(appId, out AppDefinition? after);
            if (string.Equals(before?.Fingerprint, after?.Fingerprint, StringComparison.Ordinal))
            {
                continue;
            }

            cookies.TryRemove(SchemeNames.Cookie(appId));
            oidc.TryRemove(SchemeNames.Oidc(appId));

            string? reason = before is null ? null
                : after is null ? "removed"
                : !string.Equals(before.OAuth.ClientId, after.OAuth.ClientId, StringComparison.Ordinal) ? "new client"
                : null;
            if (reason is null)
            {
                Log.Evicted(logger, appId, next.Revision);
                continue;
            }

            Purge(appId, reason, next.Revision);
        }
    }

    /// <summary>
    /// Removes every session of the app. <see cref="IRegistry.OnSwapped"/> takes a synchronous callback,
    /// so the purge is waited for here, on the thread that swapped (the registry poller, never a
    /// request). A purge that fails is logged and does not stop the other apps' eviction; the ticket
    /// store's client binding still refuses the sessions of a replaced client.
    /// </summary>
    private void Purge(string appId, string reason, long revision)
    {
        try
        {
            services.GetRequiredService<ISessionStore>().RemoveAppAsync(appId, CancellationToken.None).GetAwaiter().GetResult();
            Log.Purged(logger, appId, reason, revision);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.PurgeFailed(logger, e, appId, reason, revision);
        }
    }

    // App ids and revisions only: nothing here is a credential (contracts/telemetry.md, N-16).
    private static partial class Log
    {
        [LoggerMessage(EventId = 3601, Level = LogLevel.Information,
            Message = "Evicted the cached options of app {AppId} at registry revision {Revision}.")]
        public static partial void Evicted(ILogger logger, string appId, long revision);

        [LoggerMessage(EventId = 3602, Level = LogLevel.Information,
            Message = "Evicted the cached options and purged the sessions of app {AppId} ({Reason}) at registry revision {Revision}.")]
        public static partial void Purged(ILogger logger, string appId, string reason, long revision);

        [LoggerMessage(EventId = 3603, Level = LogLevel.Error,
            Message = "Evicted the cached options of app {AppId} ({Reason}) at registry revision {Revision}, but its sessions could not be purged.")]
        public static partial void PurgeFailed(ILogger logger, Exception exception, string appId, string reason, long revision);
    }
}
