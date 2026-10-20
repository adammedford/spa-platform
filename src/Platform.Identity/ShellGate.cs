using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// <c>UsePlatformShellGate</c>: a session before any asset (ADR-0008). An unauthenticated
/// <c>GET</c> or <c>HEAD</c> for an asset is challenged with the app's OpenID Connect scheme, and the
/// handler's events (task 3.5) turn that challenge into a redirect to the provider for a navigation and
/// a 401 problem for anything else. So whether a request is a navigation is decided in one place. With
/// no properties, the handler takes the request's own path and query as where to return after sign-in
/// (OpenIdConnectHandler.HandleChallengeAsyncInternal:
/// https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/OpenIdConnect/src/OpenIdConnectHandler.cs),
/// so a deep link survives the login.
///
/// The paths contracts/request-gate.md does not give to "everything else" pass untouched: <c>/bff</c>
/// and <c>/api</c> answer for their own sessions with 401 and never a redirect
/// (contracts/http-contract.md -> Errors), the two callbacks are the framework's, <c>/signout-oidc</c>
/// is always 404, and the health paths answer before host resolution. The list is the one the asset
/// endpoint reserves, compared the same way (ignoring case), so a path this gate lets through is one
/// on which no asset is ever served. A method other than <c>GET</c> or <c>HEAD</c> serves no asset
/// either, and passes on to the stage that answers it 405.
/// </summary>
internal static class ShellGate
{
    private static readonly PathString[] Ungated =
    [
        "/bff", "/api", "/signin-oidc", "/signout-callback-oidc", "/signout-oidc", "/healthz", "/readyz",
    ];

    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!Gated(context.Request))
        {
            await next(context);
            return;
        }

        // Fails closed: without the pinned app there is no scheme to ask, and passing the request on
        // would serve an asset without a session.
        AppDefinition app = Bff.Pinned(context, "UsePlatformShellGate");
        // KeyOf answers null unless the cookie authenticated a session this app owns, and notes a dead
        // cookie under auth.session as it does.
        AuthenticateResult session = await context.AuthenticateAsync(SchemeNames.Cookie(app.AppId));
        if (context.RequestServices.GetRequiredService<AppSessions>().KeyOf(context, session) is not null)
        {
            await next(context);
            return;
        }

        await context.ChallengeAsync(SchemeNames.Oidc(app.AppId));
    }

    private static bool Gated(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && !Ungated.Any(path => request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase));
}
