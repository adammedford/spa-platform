using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The OpenID Connect handler's events for every app (plan row 3.5). Applied after the provider's
/// profile has configured the options, so the gateway's rules wrap whatever the profile set rather
/// than being replaced by it (ADR-0011: the profile refines the provider's part, not these).
/// </summary>
internal static class AuthenticationEvents
{
    /// <summary>
    /// The claims a session keeps: the three <c>GET /bff/session</c> returns
    /// (contracts/http-contract.md -> Endpoints). Everything else the ID token carried is dropped at
    /// sign-in, so the ticket in the session store holds no more of the user than the browser may see.
    /// </summary>
    internal static readonly IReadOnlySet<string> KeptClaims = new HashSet<string>(StringComparer.Ordinal) { "sub", "name", "email" };

    /// <summary>
    /// The one page every refused login callback ends on. A constant, so it cannot carry anything
    /// from the request: not the provider's <c>error_description</c>, not the state or code, not the
    /// handler's reason. Which refusal it was is in the request line under <c>auth.callback</c>. The
    /// link starts a fresh login through the shell gate, since <c>/</c> needs a session (ADR-0008).
    /// No style or script, so the baseline content security policy needs nothing added for it.
    /// </summary>
    internal const string FailurePage =
        "<!doctype html>\n"
        + "<html lang=\"en\">\n"
        + "<head><meta charset=\"utf-8\"><title>Sign-in failed</title></head>\n"
        + "<body>\n"
        + "<h1>Sign-in could not be completed</h1>\n"
        + "<p><a href=\"/\">Return to the application</a> to sign in again.</p>\n"
        + "</body>\n"
        + "</html>\n";

    public static void Apply(OpenIdConnectOptions options)
    {
        OpenIdConnectEvents events = options.Events;
        Func<RedirectContext, Task> profileRedirect = events.OnRedirectToIdentityProvider;
        events.OnRedirectToIdentityProvider = async context =>
        {
            if (!IsNavigation(context.Request))
            {
                // ADR-0008: an unauthenticated fetch gets 401, never a redirect. A redirect to the
                // provider is useless to a fetch: it cannot follow it cross-origin, and an SPA reads
                // 401 as "navigate to /bff/login" (assumption A3). Two cases reach this writer. A fetch
                // that presented no cookie is an answer, not a rejection, and carries no rule id. A
                // fetch whose cookie resolved to no session is a rejection under auth.session, which
                // AppSessions.KeyOf noted when the shell gate asked it, before the challenge that
                // raised this event (contracts/changes/issue-160-rejection-table.md). Either way this
                // writes the body and no rule id.
                context.HandleResponse();
                WithdrawLoginState(context);
                await Problems.WriteAsync(context.HttpContext, Problems.Unauthenticated);
                return;
            }

            await profileRedirect(context);
        };

        // Every failure the handler reports ends here: unreadable or tampered state, failed
        // correlation, a refused code exchange, a nonce or token that does not validate, and the
        // provider's own error, access_denied included, which falls through to this event when no
        // AccessDeniedPath is set (RemoteAuthenticationHandler.HandleAccessDeniedErrorAsync, same
        // source as below). Not chained to a profile's handler: the page is the gateway's alone.
        events.OnRemoteFailure = async context =>
        {
            context.HandleResponse();
            // The page below ends the response, so the rule is only noted (#142, Q11).
            Rejection.Note(context.HttpContext, RuleIds.AuthCallback);
            HttpResponse response = context.Response;
            response.StatusCode = StatusCodes.Status400BadRequest;
            response.ContentType = "text/html; charset=utf-8";
            response.Headers.CacheControl = "no-store";
            await response.WriteAsync(FailurePage, context.HttpContext.RequestAborted);
        };

        Func<TicketReceivedContext, Task> profileTicket = events.OnTicketReceived;
        events.OnTicketReceived = async context =>
        {
            await profileTicket(context);
            if (context.Principal is { } principal)
            {
                context.Principal = Trimmed(principal);
            }
        };
    }

    /// <summary>
    /// One identity with the kept claims, under the original's authentication type and name and role
    /// claim types, so it is still authenticated and still reads its name the same way. The handler
    /// signs in with whatever principal this event leaves on the context
    /// (https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/Core/src/RemoteAuthenticationHandler.cs,
    /// HandleRequestAsync).
    /// </summary>
    private static ClaimsPrincipal Trimmed(ClaimsPrincipal principal)
    {
        ClaimsIdentity source = principal.Identities.First();
        ClaimsIdentity kept = new(
            principal.Claims.Where(c => KeptClaims.Contains(c.Type)).Select(c => new Claim(c.Type, c.Value, c.ValueType, c.Issuer)),
            source.AuthenticationType,
            source.NameClaimType,
            source.RoleClaimType);
        return new ClaimsPrincipal(kept);
    }

    /// <summary>
    /// The handler writes the nonce and correlation cookies before it raises this event
    /// (https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/OpenIdConnect/src/OpenIdConnectHandler.cs,
    /// HandleChallengeAsyncInternal). No login starts on a 401, so they are taken back off the
    /// response rather than left in the browser for a login that never comes.
    /// </summary>
    private static void WithdrawLoginState(RedirectContext context)
    {
        string?[] prefixes = [context.Options.CorrelationCookie.Name, context.Options.NonceCookie.Name];
        IHeaderDictionary headers = context.Response.Headers;
        headers.SetCookie = new([.. headers.SetCookie.Where(cookie => !prefixes.Any(prefix =>
            !string.IsNullOrEmpty(prefix) && cookie is not null && cookie.StartsWith(prefix, StringComparison.Ordinal)))]);
    }

    /// <summary>
    /// A navigation is what the browser says it is, read the way contracts/request-gate.md reads one
    /// for <c>/bff/login</c> (<c>gate.navigation</c>): <c>Sec-Fetch-Mode: navigate</c> and
    /// <c>Sec-Fetch-Dest: document</c>, each sent once
    /// (https://www.w3.org/TR/fetch-metadata/#sec-fetch-mode-header). A frame or object loading the
    /// shell is not one, so it is not sent to a provider that would only be framed. A request without
    /// fetch metadata is not one either: the gateway never redirects a request it cannot show is a
    /// navigation, the same reading the request gate gives a missing header (ADR-0007).
    /// </summary>
    private static bool IsNavigation(HttpRequest request) =>
        Single(request, "Sec-Fetch-Mode", "navigate") && Single(request, "Sec-Fetch-Dest", "document");

    private static bool Single(HttpRequest request, string header, string value) =>
        request.Headers[header] is { Count: 1 } values
        && string.Equals(values[0], value, StringComparison.Ordinal);
}
