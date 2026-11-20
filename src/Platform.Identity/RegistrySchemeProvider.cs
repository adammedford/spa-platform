using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The scheme provider (ADR-0001, plan row 3.1). It replaces the framework's
/// <see cref="IAuthenticationSchemeProvider"/> and answers from the app that host resolution pinned
/// for this request as <see cref="IAppFeature"/>, never from the registry directly, so one request
/// never straddles a swap (<c>contracts/pipeline.md</c>, <c>UsePlatformHostResolution</c>).
///
/// On an app's host the only schemes that exist are that app's scheme pair,
/// <see cref="SchemeNames.Oidc(string)"/> and <see cref="SchemeNames.Cookie"/>, and for a shell each of
/// its fragments' <see cref="SchemeNames.Oidc(string, string)"/> (#994). Any other name resolves to
/// nothing (S-B8, R-B6), and the only request-handling schemes are the app's own OpenID Connect
/// schemes, so the framework's <c>AuthenticationMiddleware</c> builds one remote handler per request rather
/// than one per app (S-A7, R-A6, S-F1; the stock provider built 303 at 303 apps). The middleware
/// builds a handler for every scheme <see cref="GetRequestHandlerSchemesAsync"/> returns, on every
/// request: https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/Core/src/AuthenticationMiddleware.cs.
/// A request with no pinned app (health, or a host resolution refused) has no schemes at all.
///
/// Schemes come from the registry only: <see cref="AddScheme"/> and <see cref="RemoveScheme"/> throw,
/// so nothing can register a scheme every host would share.
/// </summary>
public sealed class RegistrySchemeProvider(IHttpContextAccessor accessor) : IAuthenticationSchemeProvider
{
    private static readonly Task<AuthenticationScheme?> None = Task.FromResult<AuthenticationScheme?>(null);
    private static readonly Task<IEnumerable<AuthenticationScheme>> Empty =
        Task.FromResult<IEnumerable<AuthenticationScheme>>([]);

    // Schemes are cached per snapshot: a swap brings a new snapshot and so new scheme objects, and a
    // retired snapshot's cache goes with it, because the table holds its key weakly. Within one
    // snapshot an app's pair is built once, not on every call.
    private readonly ConditionalWeakTable<RegistrySnapshot, ConcurrentDictionary<string, SchemePair>> cache = new();

    private SchemePair? Pair
    {
        get
        {
            if (accessor.HttpContext?.Features.Get<IAppFeature>() is not { } pinned)
            {
                return null;
            }

            return cache.GetValue(pinned.Snapshot, static _ => new(StringComparer.Ordinal))
                .GetOrAdd(pinned.App.AppId, static (_, app) => new SchemePair(app), pinned.App);
        }
    }

    public Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() => Pair?.CookieTask ?? None;

    public Task<AuthenticationScheme?> GetDefaultChallengeSchemeAsync() => Pair?.OidcTask ?? None;

    public Task<AuthenticationScheme?> GetDefaultForbidSchemeAsync() => Pair?.CookieTask ?? None;

    public Task<AuthenticationScheme?> GetDefaultSignInSchemeAsync() => Pair?.CookieTask ?? None;

    public Task<AuthenticationScheme?> GetDefaultSignOutSchemeAsync() => Pair?.CookieTask ?? None;

    public Task<AuthenticationScheme?> GetSchemeAsync(string name) => Pair is not { } pair
        ? None
        : string.Equals(name, pair.Cookie.Name, StringComparison.Ordinal) ? pair.CookieTask
        : string.Equals(name, pair.Oidc.Name, StringComparison.Ordinal) ? pair.OidcTask
        : pair.Fragments.TryGetValue(name, out Task<AuthenticationScheme?>? fragment) ? fragment
        : None;

    public Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync() =>
        Pair?.RequestHandlersTask ?? Empty;

    public Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync() => Pair?.AllTask ?? Empty;

    public void AddScheme(AuthenticationScheme scheme) =>
        throw new NotSupportedException("Schemes come from the registry only (ADR-0001).");

    public void RemoveScheme(string name) =>
        throw new NotSupportedException("Schemes come from the registry only (ADR-0001).");

    /// <summary>
    /// One app's scheme pair and its fragments' OpenID Connect schemes, with the completed tasks the
    /// interface returns. The shell's scheme comes first and each fragment's follows in registry order
    /// (#994, the fragments spec #961): the middleware hands a callback to the first handler that claims
    /// it, and each fragment's claims only its own exact <c>/signin-oidc/{fragmentId}</c>, so the order
    /// decides nothing but what a caller listing them sees. Built from the pinned entry, so a snapshot's
    /// fragments are the schemes of every request pinned to it.
    /// </summary>
    private sealed class SchemePair
    {
        public SchemePair(AppDefinition app)
        {
            string appId = app.AppId;
            Cookie = new AuthenticationScheme(SchemeNames.Cookie(appId), null, typeof(PlatformCookieHandler));
            Oidc = new AuthenticationScheme(SchemeNames.Oidc(appId), null, typeof(PlatformOidcHandler));
            CookieTask = Task.FromResult<AuthenticationScheme?>(Cookie);
            OidcTask = Task.FromResult<AuthenticationScheme?>(Oidc);
            AuthenticationScheme[] fragments =
            [
                .. app.Fragments.Select(f => new AuthenticationScheme(SchemeNames.Oidc(appId, f.FragmentId), null, typeof(PlatformOidcHandler))),
            ];
            Fragments = fragments.ToDictionary(s => s.Name, s => Task.FromResult<AuthenticationScheme?>(s), StringComparer.Ordinal);
            RequestHandlersTask = Task.FromResult<IEnumerable<AuthenticationScheme>>([Oidc, .. fragments]);
            AllTask = Task.FromResult<IEnumerable<AuthenticationScheme>>([Cookie, Oidc, .. fragments]);
        }

        /// <summary>Each fragment's OpenID Connect scheme, by name.</summary>
        public Dictionary<string, Task<AuthenticationScheme?>> Fragments { get; }

        public AuthenticationScheme Cookie { get; }

        public AuthenticationScheme Oidc { get; }

        public Task<AuthenticationScheme?> CookieTask { get; }

        public Task<AuthenticationScheme?> OidcTask { get; }

        public Task<IEnumerable<AuthenticationScheme>> RequestHandlersTask { get; }

        public Task<IEnumerable<AuthenticationScheme>> AllTask { get; }
    }
}
