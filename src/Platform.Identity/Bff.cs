using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// <c>MapPlatformBff</c>'s <c>/bff/*</c>: the three endpoints of contracts/http-contract.md ->
/// Endpoints. <c>UsePlatformRequestGate</c>, which runs before authentication, has already held each to
/// its method and fetch metadata (contracts/request-gate.md), so nothing here re-checks them.
/// </summary>
internal static class Bff
{
    public const string SessionPath = "/bff/session";
    public const string LoginPath = "/bff/login";
    public const string LogoutPath = "/bff/logout";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(SessionPath, SessionAsync);
        endpoints.MapGet(LoginPath, LoginAsync);
        endpoints.MapPost(LogoutPath, LogoutAsync);
    }

    /// <summary>
    /// <c>GET /bff/session</c>: the user, the session's expiry and the antiforgery request token, or 401
    /// with a problem body and never a redirect. The claims are the three the ticket keeps (task 3.5), and
    /// no token the session holds is ever written here (N-23).
    /// </summary>
    private static async Task SessionAsync(HttpContext context)
    {
        AppDefinition app = Pinned(context, "MapPlatformBff");

        // It carries the antiforgery request token, which is per user, so no cache keeps it.
        context.Response.Headers.CacheControl = "no-store";

        if (await SessionOfAsync(context, app) is not { } record)
        {
            // An answer for no cookie, a rejection for a dead one, which KeyOf has noted; one body.
            await Problems.WriteAsync(context, Problems.Unauthenticated);
            return;
        }

        string csrfToken = context.RequestServices.GetRequiredService<ICsrfTokens>().Issue(context);
        ClaimsPrincipal user = context.User;
        await context.Response.WriteAsJsonAsync(
            new SessionAnswer(
                Sub: record.Subject,
                Name: user.FindFirst("name")?.Value,
                Email: user.FindFirst("email")?.Value,
                ExpiresAt: record.ExpiresAt,
                CsrfToken: csrfToken),
            context.RequestAborted);
    }

    /// <summary>
    /// <c>GET /bff/login</c>: discards any session this browser has on the app, then challenges. The
    /// discard is the session module's end with no action before the removal, under the session's gate,
    /// so a refresh in flight on the session finishes before the session goes and every removal the
    /// gateway itself starts runs gated-first under the one ownership check (#141 Q9). It revokes nothing:
    /// contracts/http-contract.md promises a discard, and a revocation here would be a change to that
    /// frozen behaviour and would put the provider deadline on every login (#141 Q10).
    ///
    /// The cookie handler's sign-out still follows, for the cookie it expires; the removal it makes through
    /// the ticket store finds the session already gone and does nothing (<see cref="AppSessions.RemoveAsync"/>,
    /// #141 Q12; CookieAuthenticationHandler.HandleSignOutAsync:
    /// https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/Cookies/src/CookieAuthenticationHandler.cs).
    /// A request with no session, or whose cookie resolves to a dead one, is challenged all the same, and
    /// the dead cookie is not refused: the login does what it was asked, so the request ends in no
    /// rejection and carries no rule id (#199, #185 Q18). The return path is
    /// kept only when it is local (N-15); the sign-in that follows always lands under a new session key,
    /// which the callback-blind cookie manager of task 3.3 ensures (R-D5).
    /// </summary>
    private static async Task LoginAsync(HttpContext context)
    {
        AppDefinition app = Pinned(context, "MapPlatformBff");
        string returnUrl = ReturnUrl.LocalOrRoot(context.Request.Query["returnUrl"]);

        AppSessions sessions = context.RequestServices.GetRequiredService<AppSessions>();
        AuthenticateResult session = await context.AuthenticateAsync(SchemeNames.Cookie(app.AppId));
        if (sessions.KeyOf(context, session, refusesDeadSession: false) is { } key)
        {
            // Released before the sign-out, whose ungated removal needs no gate and should wait on none.
            using AppSessions.Lease lease = await sessions.EnterAsync(app, key);
            await lease.EndAsync();
        }

        await context.SignOutAsync(SchemeNames.Cookie(app.AppId));
        await context.ChallengeAsync(SchemeNames.Oidc(app.AppId), new AuthenticationProperties { RedirectUri = returnUrl });
    }

    /// <summary>
    /// <c>POST /bff/logout</c>: ends the session at the gateway (revocation attempted, removal certain:
    /// <see cref="SessionEnd"/>), clears the antiforgery cookie and the session cookie, then hands the
    /// browser to the issuer's profile for the provider's logout, which comes back to <c>/</c> (task 3.7).
    /// A request with no session is still signed out of the provider and still clears both cookies.
    /// </summary>
    private static async Task LogoutAsync(HttpContext context)
    {
        AppDefinition app = Pinned(context, "MapPlatformBff");
        AuthenticateResult session = await context.AuthenticateAsync(SchemeNames.Cookie(app.AppId));

        // A dead cookie is not refused here: the browser is still signed out of the provider and its
        // cookies still cleared, so the request ends in no rejection and carries no rule id.
        if (context.RequestServices.GetRequiredService<AppSessions>().KeyOf(context, session, refusesDeadSession: false) is { } key)
        {
            await context.RequestServices.GetRequiredService<SessionEnd>().EndAsync(app, key);
        }

        context.RequestServices.GetRequiredService<ICsrfTokens>().Clear(context);

        // The cookie handler's sign-out expires the cookie. The OpenID Connect handler's then authenticates
        // its sign-out scheme, the app's cookie scheme, for the ID token it sends as id_token_hint, and the
        // cookie handler answers from the result it read earlier in this request, which it keeps although
        // the record has gone (OpenIdConnectHandler.SignOutAsync and AuthenticationHandler.AuthenticateAsync:
        // https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/OpenIdConnect/src/OpenIdConnectHandler.cs
        // https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/Core/src/AuthenticationHandler.cs).
        await context.SignOutAsync(SchemeNames.Cookie(app.AppId));
        await context.SignOutAsync(SchemeNames.Oidc(app.AppId), new AuthenticationProperties { RedirectUri = "/" });
    }

    /// <summary>
    /// The record of the session this request authenticated with, when it is this app's and this client's:
    /// the session module's ungated read, which holds the ownership check (#141). A read changes
    /// nothing, so it waits behind no refresh and takes the request's token. A cookie that resolved to no
    /// session is noted under <c>auth.session</c>, by <see cref="AppSessions.KeyOf"/>.
    /// </summary>
    private static async Task<SessionRecord?> SessionOfAsync(HttpContext context, AppDefinition app)
    {
        AppSessions sessions = context.RequestServices.GetRequiredService<AppSessions>();
        AuthenticateResult session = await context.AuthenticateAsync(SchemeNames.Cookie(app.AppId));
        return sessions.KeyOf(context, session) is { } key
            ? await sessions.ReadAsync(app, key, context.RequestAborted)
            : null;
    }

    /// <summary>
    /// The app host resolution pinned. Its absence means the pipeline is misordered, and a stage that
    /// guessed would answer for the wrong app or for none, so it throws.
    /// </summary>
    internal static AppDefinition Pinned(HttpContext context, string stage) =>
        context.Features.Get<IAppFeature>()?.App
        ?? throw new InvalidOperationException(
            $"{stage} runs after UsePlatformHostResolution, which sets IAppFeature (contracts/pipeline.md).");

    /// <summary>
    /// <c>auth.session</c> (contracts/request-gate.md): the request presented a session cookie and it
    /// resolved to no session for this app: removed at logout (S-E4), swept, purged, another app's
    /// (S-B7), or not decryptable at all. The cookie handler reports each as a failure, and reports no
    /// cookie as no result, which is not a rejection and leaves the rule unset
    /// (CookieAuthenticationHandler.ReadCookieTicket:
    /// https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/Cookies/src/CookieAuthenticationHandler.cs).
    ///
    /// That decision reads the framework's authentication result, so it stays here; the note is
    /// <see cref="Rejection.Note"/>'s, which writes no response, since the challenge or the 401 that
    /// follows is another writer's (#142, Q11). Called from <see cref="AppSessions.KeyOf"/> and nowhere
    /// else, so the project's one <c>auth.session</c> note has one call site (#141, S3).
    /// </summary>
    internal static void NoteDeadSession(HttpContext context, AuthenticateResult session)
    {
        if (session.Failure is not null)
        {
            Rejection.Note(context, RuleIds.AuthSession);
        }
    }

    /// <summary>The body of <c>GET /bff/session</c>, written in camel case by the web defaults.</summary>
    private sealed record SessionAnswer(string Sub, string? Name, string? Email, DateTimeOffset ExpiresAt, string CsrfToken);
}

/// <summary>
/// The <c>returnUrl</c> of <c>/bff/login</c>: "must be a local path, or it becomes <c>/</c>"
/// (contracts/http-contract.md). Local means one value, starting with a single <c>/</c> that no second
/// <c>/</c> or <c>\</c> follows, since browsers read both <c>//host</c> and <c>/\host</c> as another
/// origin (N-15), and carrying no <c>\</c> or control character anywhere, so nothing a browser would
/// rewrite reaches the <c>Location</c> header. The framework's own rule for a local URL
/// (<c>Microsoft.AspNetCore.Mvc.Routing.UrlHelperBase.CheckIsLocalUrl</c>) is MVC's and not referenced
/// here; this is narrower than it, since it also refuses <c>~/</c> and any backslash.
/// </summary>
internal static class ReturnUrl
{
    public static string LocalOrRoot(StringValues values) =>
        values is { Count: 1 } && values[0] is { } value && IsLocal(value) ? value : "/";

    internal static bool IsLocal(string value) =>
        value.Length > 0
        && value[0] == '/'
        && (value.Length == 1 || value[1] is not ('/' or '\\'))
        && !value.Any(c => c == '\\' || char.IsControl(c));
}
