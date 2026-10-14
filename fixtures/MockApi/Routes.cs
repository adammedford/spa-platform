using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MockApi;

/// <summary>
/// The three routes of task 5.3. Authentication has already checked the signature, the issuer, the
/// expiry, <c>token_use</c> and the audience where this API binds one; what is left is the API
/// owner's own two decisions, which client it accepts and which scope a route needs.
/// </summary>
internal static class Routes
{
    /// <summary>
    /// Headers /echo reports as present without reporting their value. The echo body travels back
    /// through the gateway to the browser, and F-05 dumps every response body and refuses to find a
    /// token-shaped string in any of them. Returning the session's bearer token here would hand it
    /// to the page the whole architecture keeps it away from.
    ///
    /// Presence is what the negative tests need: the upstream contract's claim is about which
    /// headers arrive, never about what they contain.
    /// </summary>
    private static readonly HashSet<string> Redacted = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Cookie",
        "X-CSRF-TOKEN",
    };

    internal static void Map(WebApplication app)
    {
        // Who the caller is, as this API sees them. F-08 reads the client, the scopes and the bound
        // audience from here, which is the only way to tell app 1's token from app 2's at a listener.
        app.MapGet("/whoami", (HttpContext context, ApiListenerOptions api, ClientAdmission admission) =>
            Guard(context, api, admission, api.ReadScope) ?? Results.Json(new
            {
                api = api.Name,
                clientId = ClientOf(context),
                subject = context.User.FindFirst("sub")?.Value,
                scopes = ScopesOf(context),
                audience = context.User.FindFirst("aud")?.Value,
            }));

        // What arrived. The upstream contract says what may and may not reach here, and the deployed
        // negative tests read this to check the gateway stripped what it was supposed to.
        app.Map("/echo", async (HttpContext context, ApiListenerOptions api, ClientAdmission admission) =>
        {
            if (Guard(context, api, admission, api.ReadScope) is { } refusal)
            {
                return refusal;
            }

            using StreamReader reader = new(context.Request.Body, Encoding.UTF8);

            return Results.Json(new
            {
                api = api.Name,
                method = context.Request.Method,
                path = context.Request.Path.Value,
                query = context.Request.QueryString.Value,
                headers = context.Request.Headers.ToDictionary(
                    header => header.Key,
                    header => Redacted.Contains(header.Key) ? ["(redacted)"] : header.Value.ToArray()!,
                    StringComparer.OrdinalIgnoreCase),
                body = await reader.ReadToEndAsync(context.RequestAborted),
            });
        });

        // The route no test app may reach. Its scope exists on the resource server and no app asks
        // for it, so F-08's 403 is a scope decision rather than a missing route.
        app.MapGet("/admin", (HttpContext context, ApiListenerOptions api, ClientAdmission admission) =>
            Guard(context, api, admission, api.AdminScope) ?? Results.Json(new { api = api.Name, admin = true }));
    }

    /// <summary>
    /// The two checks that are this API's own, in the order their answers differ: an unauthenticated
    /// or unadmitted caller learns nothing about scopes.
    /// </summary>
    private static IResult? Guard(HttpContext context, ApiListenerOptions api, ClientAdmission admission, string scope)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Refused(StatusCodes.Status401Unauthorized, "invalid_token", api);
        }

        // Admission is the API owner's decision, not the gateway's. The gateway will have issued a
        // perfectly good token for a client this API has never agreed to serve (F-07, N-17).
        if (!admission.IsAdmitted(ClientOf(context)))
        {
            return Refused(StatusCodes.Status403Forbidden, "client_not_admitted", api);
        }

        return ScopesOf(context).Contains(scope, StringComparer.Ordinal)
            ? null
            : Refused(StatusCodes.Status403Forbidden, "insufficient_scope", api);
    }

    /// <summary>
    /// The refusal, with the reason in the body as well as the status. N-17 expects two different
    /// refusals from the two listeners, so a test has to be able to tell them apart.
    /// </summary>
    private static IResult Refused(int status, string error, ApiListenerOptions api) =>
        Results.Json(new { api = api.Name, error }, statusCode: status);

    /// <summary>
    /// Cognito puts the client in <c>client_id</c>. The claim is read through
    /// <see cref="ClaimsPrincipal"/> rather than parsed, because the handler has already done that.
    /// </summary>
    private static string? ClientOf(HttpContext context) => context.User.FindFirst("client_id")?.Value;

    /// <summary>The <c>scope</c> claim, which is space-delimited in an access token.</summary>
    private static string[] ScopesOf(HttpContext context) =>
        (context.User.FindFirst("scope")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
