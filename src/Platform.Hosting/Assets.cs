using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Platform.Abstractions;

namespace Platform.Hosting;

/// <summary>
/// Stage 10's asset endpoint (plan row 2.4; contracts/s3-layout.md; contracts/http-contract.md): every
/// <c>GET</c> or <c>HEAD</c> that is not <c>/bff/*</c> or <c>/api/*</c>, read from the app's live
/// release prefix and streamed back.
///
/// The key is built from two registry values and a path this class has validated itself, never from
/// a segment it has not checked (s3-layout.md). The request gate (stage 6, task 4.3) refuses the same
/// paths first in the composed gateway; this endpoint does not rely on it, because the key is its own
/// and N-13 is about what it reads, whichever stage refused.
/// </summary>
internal sealed partial class Assets(ILogger logger)
{
    private const string IndexHtml = "index.html";

    /// <summary>Under this prefix of a release, files are immutable and cached for a year (ADR-0009).</summary>
    private const string ImmutablePrefix = "assets/";

    private const string ImmutableCacheControl = "private, max-age=31536000, immutable";

    // no-cache with an ETag: the browser keeps the copy and revalidates on every use, which is what
    // makes a release switch visible at the next navigation (ADR-0009). private on both, because every
    // asset needs a session (ADR-0008), so no shared cache may keep one.
    private const string RevalidateCacheControl = "private, no-cache";

    /// <summary>
    /// The paths an SPA cannot serve (contracts/http-contract.md, Endpoints). They are matched without
    /// regard to case, as endpoint routing and the request gate match them
    /// (https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.pathstring.startswithsegments):
    /// <c>/BFF/x</c> is the platform's path in another case, not a file the SPA may ship.
    /// </summary>
    private static readonly PathString[] Reserved =
    [
        "/bff", "/api", "/signin-oidc", "/signout-callback-oidc", "/signout-oidc", "/healthz", "/readyz",
    ];

    // The framework's own extension table, so the type an object was stored with is never read
    // (s3-layout.md: content type comes from the file extension).
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public async Task InvokeAsync(HttpContext context)
    {
        // Stage 4 pins the app. Without it there is no release to read from, and guessing one would be
        // serving another app's files, so a misordered pipeline throws rather than answers.
        AppDefinition app = (context.Features.Get<IAppFeature>()
            ?? throw new InvalidOperationException(
                "The asset endpoint (stage 10) runs after host resolution (stage 4), which sets IAppFeature.")).App;

        HttpRequest request = context.Request;
        bool head = HttpMethods.IsHead(request.Method);
        if (!head && !HttpMethods.IsGet(request.Method))
        {
            // The request gate refuses this first (rule gate.method). The endpoint takes every method so
            // that, without the gate, the answer is still the contract's 405 problem.
            context.Response.Headers.Allow = "GET, HEAD";
            await Problems.WriteAsync(context, StatusCodes.Status405MethodNotAllowed, Problems.MethodNotAllowed);
            return;
        }

        if (IsReserved(request.Path))
        {
            LogRefused(logger, "path.reserved");
            await Problems.WriteAsync(context, StatusCodes.Status404NotFound, Problems.NotFound);
            return;
        }

        // RawTarget is empty only when no server set it (a TestServer), and then there is nothing
        // undecoded to judge; the decoded path is judged either way.
        string? raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        string? relative = string.IsNullOrEmpty(raw) || AssetPath.RawIsNormal(raw)
            ? AssetPath.Validate(request.Path.Value)
            : null;
        if (relative is null)
        {
            LogRefused(logger, "path.invalid");
            await Problems.WriteAsync(context, StatusCodes.Status400BadRequest, Problems.InvalidRequest);
            return;
        }

        if (relative.Length == 0 || (!AssetPath.HasExtension(relative) && AcceptsHtml(request)))
        {
            relative = IndexHtml;
        }

        // AppId and Release are constrained by registry.schema.json to characters that cannot leave
        // their segment, and the registry validator (task 2.1) rejects a document that breaks them,
        // so the prefix is fixed before the request path is appended.
        string key = $"apps/{app.AppId}/releases/{app.Release}/{relative}";

        string? ifNoneMatch = StringValues.IsNullOrEmpty(request.Headers.IfNoneMatch)
            ? null
            : request.Headers.IfNoneMatch.ToString();

        IObjectStore store = context.RequestServices.GetRequiredService<IObjectStore>();
        StoredObject? stored = await store.GetAsync(key, ifNoneMatch, context.RequestAborted);
        if (stored is null)
        {
            await Problems.WriteAsync(context, StatusCodes.Status404NotFound, Problems.NotFound);
            return;
        }

        await using Stream? body = stored.Body;

        HttpResponse response = context.Response;

        // The policy follows the key served, not the path asked for: an extensionless path under
        // /assets/ that falls back to index.html is the shell, and must not be cached for a year.
        response.Headers.CacheControl = relative.StartsWith(ImmutablePrefix, StringComparison.Ordinal)
            ? ImmutableCacheControl
            : RevalidateCacheControl;
        response.Headers.ETag = stored.ETag;

        // Also sent by stage 5 on every response; set here too because a type chosen by extension is
        // only safe if the browser is told not to second-guess it (s3-layout.md).
        response.Headers.XContentTypeOptions = "nosniff";

        if (stored.NotModified)
        {
            // A 304 carries the ETag and Cache-Control a 200 would have (RFC 9110, section 15.4.5).
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ContentTypes.TryGetContentType(relative, out string? type) ? type : "application/octet-stream";
        response.ContentLength = stored.Length;

        if (head || body is null)
        {
            // HEAD answers with the headers GET would send, Content-Length included, and no body
            // (RFC 9110, section 9.3.2).
            return;
        }

        await body.CopyToAsync(response.Body, context.RequestAborted);
    }

    private static bool IsReserved(PathString path)
    {
        foreach (PathString reserved in Reserved)
        {
            if (path.StartsWithSegments(reserved, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// "A path with no extension that accepts HTML" (contracts/http-contract.md). A request accepts
    /// HTML when it sends no <c>Accept</c> at all, or a range that matches <c>text/html</c> with a
    /// non-zero quality: <c>text/html</c>, <c>text/*</c> or <c>*/*</c> (RFC 9110, section 12.5.1).
    /// </summary>
    private static bool AcceptsHtml(HttpRequest request)
    {
        if (StringValues.IsNullOrEmpty(request.Headers.Accept))
        {
            return true;
        }

        if (!MediaTypeHeaderValue.TryParseList(request.Headers.Accept, out IList<MediaTypeHeaderValue>? ranges))
        {
            return false;
        }

        foreach (MediaTypeHeaderValue range in ranges)
        {
            if (range.Quality is 0)
            {
                continue;
            }

            if (range.MatchesAllTypes
                || (range.Type.Equals("text", StringComparison.OrdinalIgnoreCase)
                    && (range.MatchesAllSubTypes || range.SubType.Equals("html", StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        return false;
    }

    // The rule id only (contracts/request-gate.md, Rule ids). The path is not logged: it is the
    // client's text, and the telemetry contract's request line already carries the route.
    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "Asset request refused under {Rule}")]
    private static partial void LogRefused(ILogger logger, string rule);
}
