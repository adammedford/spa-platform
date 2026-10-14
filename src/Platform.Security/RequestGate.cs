using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Platform.Abstractions;

namespace Platform.Security;

/// <summary>
/// Stage 6, the request gate: <c>contracts/request-gate.md</c>, one rule id per refusal, with the
/// status and code from <c>contracts/http-contract.md</c> -> Errors. Task 4.3; tests N-07, N-25, N-27
/// and the escape paths of N-10. ADR-0007 is why it judges <c>Sec-Fetch-Site</c> and <c>Origin</c>
/// rather than trusting <c>SameSite</c>: a sibling SPA under the same parent domain is same-site.
///
/// It runs before authentication because no rule here needs the user. Two rows of the contract's
/// table are finished by later stages and are not checked here: the session that "everything else"
/// needs is stage 8's (the shell gate, workstream 3), and the <c>X-CSRF-TOKEN</c> on unsafe
/// <c>/api</c> methods is stage 9's (task 4.4), because both need the user.
///
/// The checks run in this order: the path's form, the cookie names, then the path's own row. Within a
/// row the origin checks come before the method, so a cross-site caller learns nothing about which
/// methods a path takes, and N-07 holds whatever method it sends.
/// </summary>
internal sealed partial class RequestGate(ILogger logger)
{
    /// <summary>The prefix every platform cookie carries (<c>contracts/http-contract.md</c> -> Cookies).</summary>
    internal const string PlatformCookiePrefix = "__Host-spa-";

    private const string SameOrigin = "same-origin";

    private enum Row
    {
        Api,
        BffSession,
        BffLogin,
        BffLogout,
        Callback,
        Reserved,
        Asset,
    }

    // PathString.StartsWithSegments compares without regard to case, as endpoint routing and the
    // OpenID Connect handler's path checks do. A case-sensitive match here would let /BFF/session
    // reach the session endpoint as an "everything else" request, past the Sec-Fetch-Site check.
    // https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.pathstring.startswithsegments
    private static readonly PathString Api = "/api";
    private static readonly PathString Bff = "/bff";
    private static readonly PathString BffSession = "/bff/session";
    private static readonly PathString BffLogin = "/bff/login";
    private static readonly PathString BffLogout = "/bff/logout";
    private static readonly PathString SignInCallback = "/signin-oidc";
    private static readonly PathString SignOutCallback = "/signout-callback-oidc";
    private static readonly PathString RemoteSignOut = "/signout-oidc";

    internal Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Stage 4 pins the app before this stage runs. Without it there is no app origin to judge
        // against, and passing the request on would be failing open, so a misordered pipeline throws.
        IAppFeature app = context.Features.Get<IAppFeature>()
            ?? throw new InvalidOperationException(
                "The request gate (stage 6) runs after host resolution (stage 4), which sets IAppFeature.");

        HttpRequest request = context.Request;

        if (!PathIsNormal(context))
        {
            return RefuseAsync(context, StatusCodes.Status400BadRequest, "invalid_request", "path.invalid");
        }

        if (CookieNameImitatesPlatformCookie(request.Headers.Cookie))
        {
            return RefuseAsync(context, StatusCodes.Status400BadRequest, "invalid_request", "gate.cookie-name");
        }

        Row row = Classify(request.Path);
        switch (row)
        {
            case Row.Reserved:
                // /signout-oidc: the framework's remote sign-out is switched off (threat T18, N-27), and
                // a /bff path that is none of the three endpoints is the platform's, not the SPA's.
                return RefuseAsync(context, StatusCodes.Status404NotFound, "not_found", "path.reserved");

            case Row.Callback:
                return MethodIs(context, HttpMethods.Get) ? next(context) : RefuseMethodAsync(context, HttpMethods.Get);

            case Row.Asset:
                return MethodIs(context, HttpMethods.Get) || MethodIs(context, HttpMethods.Head)
                    ? next(context)
                    : RefuseMethodAsync(context, HttpMethods.Get, HttpMethods.Head);
        }

        // /api and /bff: same-origin browser requests only.
        if (!FetchSiteAllowed(request.Headers["Sec-Fetch-Site"], allowNone: row == Row.BffLogin))
        {
            return RefuseAsync(context, StatusCodes.Status403Forbidden, "origin_rejected", "gate.fetch-site");
        }

        if (!OriginAllowed(request.Headers.Origin, AppOrigin(request, app.App), required: row == Row.BffLogout))
        {
            return RefuseAsync(context, StatusCodes.Status403Forbidden, "origin_rejected", "gate.origin");
        }

        switch (row)
        {
            case Row.BffSession when !MethodIs(context, HttpMethods.Get):
            case Row.BffLogin when !MethodIs(context, HttpMethods.Get):
                return RefuseMethodAsync(context, HttpMethods.Get);

            case Row.BffLogout when !MethodIs(context, HttpMethods.Post):
                return RefuseMethodAsync(context, HttpMethods.Post);

            case Row.BffLogin when !IsDocumentNavigation(request.Headers):
                return RefuseAsync(context, StatusCodes.Status403Forbidden, "origin_rejected", "gate.navigation");
        }

        // /api takes every method stage 1's allowlist lets through.
        return next(context);
    }

    private static Row Classify(PathString path)
    {
        if (path.StartsWithSegments(Api))
        {
            return Row.Api;
        }

        if (path.StartsWithSegments(Bff))
        {
            if (path.StartsWithSegments(BffSession))
            {
                return Row.BffSession;
            }

            if (path.StartsWithSegments(BffLogin))
            {
                return Row.BffLogin;
            }

            return path.StartsWithSegments(BffLogout) ? Row.BffLogout : Row.Reserved;
        }

        if (path.StartsWithSegments(RemoteSignOut))
        {
            return Row.Reserved;
        }

        return path.StartsWithSegments(SignInCallback) || path.StartsWithSegments(SignOutCallback)
            ? Row.Callback
            : Row.Asset;
    }

    /// <summary>
    /// Rule <c>path.invalid</c>. Finding T-C10: YARP forwards an encoded slash or backslash, a
    /// <c>..;</c> segment and a doubled slash to the app's own upstream as sent, where a server that
    /// reads them differently could resolve another resource. Kestrel removes plain dot segments from
    /// <c>Request.Path</c> but leaves <c>%2F</c> encoded and the raw target untouched, so the check
    /// reads the raw target, before any decoding.
    /// https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.features.ihttprequestfeature.rawtarget
    /// </summary>
    internal static bool PathIsNormal(HttpContext context)
    {
        string? raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw))
        {
            raw = context.Request.PathBase.Add(context.Request.Path).ToUriComponent();
        }

        // Origin form only. The absolute form (GET http://host/path) and the asterisk form are legal
        // HTTP but no browser sends them to an origin server, and the ALB forwards the origin form.
        if (raw[0] != '/')
        {
            return false;
        }

        int query = raw.IndexOf('?', StringComparison.Ordinal);
        ReadOnlySpan<char> path = query < 0 ? raw : raw.AsSpan(0, query);

        if (path.Contains('\\'))
        {
            return false;
        }

        // Escapes of the characters that change a path's structure: '/', '\' and '.'. Kestrel refuses an
        // escaped NUL itself, before any middleware runs.
        for (int i = path.IndexOf('%'); i >= 0 && i + 2 < path.Length; i = NextEscape(path, i + 1))
        {
            ReadOnlySpan<char> hex = path.Slice(i + 1, 2);
            if (hex.Equals("2f", StringComparison.OrdinalIgnoreCase) || hex.Equals("5c", StringComparison.OrdinalIgnoreCase)
                || hex.Equals("2e", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Segments: none empty but the last (a trailing slash), and none that is '.' or '..', with or
        // without a ';' parameter after it, which some servers strip before resolving the segment.
        int segmentStart = 1;
        while (segmentStart <= path.Length)
        {
            int length = path[segmentStart..].IndexOf('/');
            bool last = length < 0;
            ReadOnlySpan<char> segment = last ? path[segmentStart..] : path.Slice(segmentStart, length);

            if (segment.IsEmpty && !last)
            {
                return false;
            }

            int parameter = segment.IndexOf(';');
            ReadOnlySpan<char> name = parameter < 0 ? segment : segment[..parameter];
            if (name is "." or "..")
            {
                return false;
            }

            if (last)
            {
                break;
            }

            segmentStart += length + 1;
        }

        return true;
    }

    private static int NextEscape(ReadOnlySpan<char> path, int from)
    {
        int next = path[from..].IndexOf('%');
        return next < 0 ? -1 : from + next;
    }

    /// <summary>
    /// Rule <c>gate.cookie-name</c>, the rule that covers every path. Browsers enforce the
    /// <c>__Host-</c> prefix, but ASP.NET Core matches request cookie names without regard to case, so
    /// a sibling host that plants <c>__host-spa-session</c> for the parent domain would be read as the
    /// session cookie. Two names equal but for case are the same cookie to the framework, so they
    /// count as one cookie sent twice (N-25).
    ///
    /// It reads the raw <c>Cookie</c> header, because the parsed collection keeps one of two equal
    /// names and so cannot see the second.
    /// </summary>
    internal static bool CookieNameImitatesPlatformCookie(StringValues cookieHeaders)
    {
        HashSet<string>? seen = null;
        foreach (string? header in cookieHeaders)
        {
            if (header is null)
            {
                continue;
            }

            foreach (string pair in header.Split(';'))
            {
                int equals = pair.IndexOf('=', StringComparison.Ordinal);
                string name = (equals < 0 ? pair : pair[..equals]).Trim();
                if (!name.StartsWith(PlatformCookiePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!name.StartsWith(PlatformCookiePrefix, StringComparison.Ordinal))
                {
                    return true;
                }

                seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!seen.Add(name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Rule <c>gate.fetch-site</c>. Absent is a refusal, not a pass, and <c>same-site</c> is refused
    /// like <c>cross-site</c>, because that is what a sibling SPA's page produces (ADR-0007).
    /// <c>none</c> is a navigation the user started, which only <c>/bff/login</c> takes.
    /// https://www.w3.org/TR/fetch-metadata/#sec-fetch-site-header
    /// </summary>
    private static bool FetchSiteAllowed(StringValues values, bool allowNone) =>
        values.Count == 1
        && (string.Equals(values[0], SameOrigin, StringComparison.Ordinal)
            || (allowNone && string.Equals(values[0], "none", StringComparison.Ordinal)));

    /// <summary>
    /// Rule <c>gate.origin</c>: an <c>Origin</c>, when sent, equals the app origin, and
    /// <c>/bff/logout</c> needs one, because a form post carries no token. An opaque origin
    /// (<c>null</c>) equals nothing.
    /// </summary>
    private static bool OriginAllowed(StringValues values, string appOrigin, bool required) =>
        values.Count switch
        {
            0 => !required,
            1 => string.Equals(values[0], appOrigin, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    /// <summary>
    /// The origin a browser on this app's page sends: the scheme stage 1 settled, which is https behind
    /// the ALB, and the registry host stage 4 pinned. Host names compare without regard to case.
    /// </summary>
    private static string AppOrigin(HttpRequest request, AppDefinition app) => request.Scheme + "://" + app.Host;

    /// <summary>Rule <c>gate.navigation</c>: <c>/bff/login</c> is a top-level navigation.</summary>
    private static bool IsDocumentNavigation(IHeaderDictionary headers) =>
        headers["Sec-Fetch-Mode"] is { Count: 1 } mode && string.Equals(mode[0], "navigate", StringComparison.Ordinal)
        && headers["Sec-Fetch-Dest"] is { Count: 1 } dest && string.Equals(dest[0], "document", StringComparison.Ordinal);

    private static bool MethodIs(HttpContext context, string method) =>
        string.Equals(context.Request.Method, method, StringComparison.Ordinal);

    private Task RefuseMethodAsync(HttpContext context, params string[] allowed)
    {
        // RFC 9110 section 15.5.6: a 405 carries Allow, here the methods this path takes.
        context.Response.Headers.Allow = string.Join(", ", allowed);
        return RefuseAsync(context, StatusCodes.Status405MethodNotAllowed, "method_not_allowed", "gate.method");
    }

    private Task RefuseAsync(HttpContext context, int status, string code, string rule)
    {
        // The rule id only. Nothing the client sent goes in the line: the path can carry a code, and
        // a cookie name is the client's to choose (contracts/telemetry.md -> Never logged).
        Refused(logger, rule);

        // Task 4.3a: the same id on the one request line stage 2 writes, and the field it counts
        // security.rejections by (contracts/telemetry.md -> "How `rule` and `session_ref` reach the
        // request line"). The same `rule` the line above logged, so the two cannot drift. Null
        // conditional because this project's own tests run with no stage 2 to create the feature.
        context.Features.Get<IRequestTelemetryFeature>()?.Rule = rule;

        return Problems.WriteAsync(context, status, code);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The request gate refused the request. rule={Rule}")]
    private static partial void Refused(ILogger logger, string rule);
}
