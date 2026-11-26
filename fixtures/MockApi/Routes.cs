using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace MockApi;

/// <summary>
/// The three routes of task 5.3, and <c>/pad</c> for G9's O9 (#1545). Authentication has already checked the signature, the issuer, the
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
            Guard(context, api, admission, "/whoami", api.ReadScope) ?? Results.Json(new
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
            if (Guard(context, api, admission, "/echo", api.ReadScope) is { } refusal)
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
            Guard(context, api, admission, "/admin", api.AdminScope) ?? Results.Json(new { api = api.Name, admin = true }));

        // Padding response headers of a size the caller chooses, for O9's header limits (#1545,
        // decided on #1530). The guard runs before the query is read, so an unauthenticated or
        // unadmitted caller learns nothing about it, as on /whoami.
        app.MapGet("/pad", (HttpContext context, ApiListenerOptions api, ClientAdmission admission) =>
            Guard(context, api, admission, "/pad", api.ReadScope) ?? Pad(context, api));
    }

    /// <summary>
    /// The largest padding value, and the largest sum of them in one response: 1 MiB, the range
    /// #1545 asks for so that every limit O9 probes is passed (the ALB's 16 K for one header and 32 K
    /// for the whole response header among them), and small enough that a caller cannot make the
    /// listener build an unbounded response.
    /// </summary>
    internal const int PadMaxTotalBytes = 1_048_576;

    /// <summary>
    /// The most padding headers in one response. O9's many-headers probe needs only enough smaller
    /// headers to pass the 32 K total; the cap keeps one call from naming a million headers.
    /// </summary>
    internal const int PadMaxCount = 1_024;

    /// <summary>
    /// <c>GET /pad?bytes=N&amp;count=M</c>: 200 with M response headers <c>X-Pad-1</c> to
    /// <c>X-Pad-M</c>, each value exactly N bytes of the letter <c>x</c>. The route exists because
    /// nothing else on the cell answers a response header of a size the caller chooses, and O9 must
    /// find the largest one the ALB and each browser accept, for one header (the merged CSP is one)
    /// and for many (#1545, #961 O9). A repeated letter is a value no token search matches: N-16's
    /// canaries and F-05's JWT shape both need characters it never has. M defaults to 1; N is
    /// required. N from 1 to <see cref="PadMaxTotalBytes"/>, M from 1 to <see cref="PadMaxCount"/>
    /// and N x M at most <see cref="PadMaxTotalBytes"/>, or the answer is 400.
    /// </summary>
    private static IResult Pad(HttpContext context, ApiListenerOptions api)
    {
        // M is at least 1, so the total bounds N as well: N alone needs no bound of its own.
        if (!TryReadCount(context.Request.Query, "bytes", required: true, out int bytes)
            || !TryReadCount(context.Request.Query, "count", required: false, out int count)
            || count > PadMaxCount
            || (long)bytes * count > PadMaxTotalBytes)
        {
            return Results.Json(new { api = api.Name, error = "invalid_padding" }, statusCode: StatusCodes.Status400BadRequest);
        }

        // One string shared by every header: the response holds N bytes of value, not N x M.
        string value = new('x', bytes);
        for (int index = 1; index <= count; index++)
        {
            context.Response.Headers[$"X-Pad-{index.ToString(CultureInfo.InvariantCulture)}"] = value;
        }

        return Results.Json(new { api = api.Name, bytes, count });
    }

    /// <summary>
    /// One query parameter as a whole number of at least 1 that fits an <see cref="int"/>: present
    /// once, digits only (no sign, space or decimal point), or absent when it is not
    /// <paramref name="required"/>, which reads as 1.
    /// </summary>
    private static bool TryReadCount(IQueryCollection query, string name, bool required, out int value)
    {
        value = 1;
        StringValues given = query[name];
        if (given.Count == 0)
        {
            return !required;
        }

        return given.Count == 1
            && int.TryParse(given[0], NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= 1;
    }

    /// <summary>
    /// The two checks that are this API's own, in the order their answers differ: an unauthenticated
    /// or unadmitted caller learns nothing about scopes. Admission is judged on the route called, so
    /// one listener can admit a shell's client on one route and a fragment's on another (#998).
    /// </summary>
    private static IResult? Guard(HttpContext context, ApiListenerOptions api, ClientAdmission admission, string route, string scope)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Refused(StatusCodes.Status401Unauthorized, "invalid_token", api);
        }

        // Admission is the API owner's decision, not the gateway's. The gateway will have issued a
        // perfectly good token for a client this API has never agreed to serve (F-07, N-17).
        if (!admission.IsAdmitted(ClientOf(context), route))
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
