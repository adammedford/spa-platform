using Microsoft.AspNetCore.Http;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The app a scheme's options are built for, shared by the OpenID Connect and cookie options setups.
/// </summary>
internal static class SchemeApp
{
    /// <summary>
    /// The app host resolution pinned on the current request when its id is <paramref name="appId"/>,
    /// so a request never straddles a registry swap; otherwise the live snapshot's entry, or null.
    /// </summary>
    public static AppDefinition? Find(IHttpContextAccessor accessor, IRegistry registry, string appId)
    {
        if (accessor.HttpContext?.Features.Get<IAppFeature>() is { } pinned
            && string.Equals(pinned.App.AppId, appId, StringComparison.Ordinal))
        {
            return pinned.App;
        }

        return registry.Current.ByAppId.TryGetValue(appId, out AppDefinition? app) ? app : null;
    }
}
