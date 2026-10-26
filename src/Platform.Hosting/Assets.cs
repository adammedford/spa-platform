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
/// The asset endpoint, <c>MapPlatformAssets</c> (plan row 2.4; contracts/s3-layout.md;
/// contracts/http-contract.md): every <c>GET</c> or <c>HEAD</c> that is not <c>/bff/*</c> or
/// <c>/api/*</c>, read from the app's live release prefix and streamed back.
///
/// The key is built from two registry values and a path this class has validated itself, never from
/// a segment it has not checked (s3-layout.md). The request gate (<c>UsePlatformRequestGate</c>,
/// task 4.3) refuses the same paths first in the composed gateway; this endpoint does not rely on
/// it, because the key is its own and N-13 is about what it reads, whichever stage refused.
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

    // Every 404 this endpoint answers (contracts/http-contract.md, the asset row; change request
    // contracts/changes/issue-381-object-read-refusal.md, item 4). RFC 9110 section 15.5.5 lets a cache
    // keep a 404 heuristically, and under the deployed role every missing asset is a refused read, so a
    // 404 a passing permission fault caused must not outlive the fault.
    private const string NotFoundCacheControl = "private, no-store";

    // The values of IRequestTelemetryFeature.ReadRefused: which kind of key was refused, never which key.
    private const string RefusedShell = "shell";
    private const string RefusedAsset = "asset";

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
    private static readonly MediaTypeHeaderValue HtmlType = new("text/html");

    public async Task InvokeAsync(HttpContext context)
    {
        // UsePlatformHostResolution pins the app. Without it there is no release to read from, and
        // guessing one would be serving another app's files, so a misordered pipeline throws rather
        // than answers.
        AppDefinition app = (context.Features.Get<IAppFeature>()
            ?? throw new InvalidOperationException(
                "The asset endpoint, MapPlatformAssets, runs after UsePlatformHostResolution, which sets IAppFeature.")).App;

        HttpRequest request = context.Request;
        bool head = HttpMethods.IsHead(request.Method);
        if (!head && !HttpMethods.IsGet(request.Method))
        {
            // The request gate refuses this first, under the same rule. The endpoint takes every method
            // so that, without the gate, the answer is still the contract's 405 problem, and it ends
            // under gate.method, the id the rule-id table gives the case, so that it reaches the
            // request line and the count as every other rejection does (#142, Q14). No Warning line of
            // this endpoint's own: the gate logs the method refusals it makes, and the pinned line
            // below names this endpoint's path checks only.
            await Rejection.EndMethodAsync(context, HttpMethods.Get, HttpMethods.Head);
            return;
        }

        if (IsReserved(request.Path))
        {
            context.Response.Headers.CacheControl = NotFoundCacheControl;
            await RejectAsync(context, RuleIds.PathReserved);
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
            await RejectAsync(context, RuleIds.PathInvalid);
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

        // The shell is the resolved key whose release-relative path is exactly index.html, compared
        // ordinally: /, /index.html and every extensionless fallback, one under /assets/ included, but
        // not /Index.html or /assets/index.html (change request issue-381-object-read-refusal, item 3).
        bool shell = string.Equals(relative, IndexHtml, StringComparison.Ordinal);

        IObjectStore store = context.RequestServices.GetRequiredService<IObjectStore>();
        StoredObject? stored;
        try
        {
            stored = await store.GetAsync(key, ifNoneMatch, context.RequestAborted);
        }
        catch (ObjectReadRefusedException)
        {
            // A refusal is ambiguous: a missing key under a role without s3:ListBucket, or a key the
            // role may not read (IObjectStore.GetAsync). The request line carries which kind of key it
            // was (contracts/telemetry.md, read_refused), never the key, which holds the client's path.
            if (context.Features.Get<IRequestTelemetryFeature>() is { } telemetry)
            {
                telemetry.ReadRefused = shell ? RefusedShell : RefusedAsset;
            }

            if (shell)
            {
                // An unreadable shell is a page that does not load, whatever the cause, and a 404 would
                // pass for an ordinary missing file: it stays the failure it is. The exception names the
                // release's index.html key and no client path.
                throw;
            }

            // Under the deployed role this is how every missing asset arrives, so it answers as one.
            await NotFoundAsync(context);
            return;
        }

        if (stored is null)
        {
            await NotFoundAsync(context);
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

        // Also sent by UsePlatformSecurityHeaders on every response; set here too because a type
        // chosen by extension is only safe if the browser is told not to second-guess it
        // (s3-layout.md).
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

    /// <summary>
    /// 404 <c>not_found</c>, never cached. An answer, not a rejection: no rule id, only the code
    /// (#142).
    /// </summary>
    private static Task NotFoundAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = NotFoundCacheControl;
        return Problems.WriteAsync(context, Problems.NotFound);
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

        // RFC 9110 section 12.5.1: the most specific matching range supplies the quality. A
        // text/html;q=0 exclusion therefore beats a positive text/* or */* (F-06).
        // IsSubsetOf matches type and parameters, ignoring the quality parameter:
        // https://learn.microsoft.com/dotnet/api/microsoft.net.http.headers.mediatypeheadervalue.issubsetof
        int specificity = -1;
        double quality = 0;
        foreach (MediaTypeHeaderValue range in ranges)
        {
            if (!HtmlType.IsSubsetOf(range))
            {
                continue;
            }

            int candidate = range.MatchesAllTypes ? 0 : range.MatchesAllSubTypes ? 1 : 2;
            if (candidate > specificity)
            {
                specificity = candidate;
                quality = range.Quality ?? 1;
            }
            else if (candidate == specificity)
            {
                quality = Math.Max(quality, range.Quality ?? 1);
            }
        }

        return quality > 0;
    }

    /// <summary>
    /// Rejects a path under its rule id: this endpoint's Warning line, then
    /// <see cref="Rejection.EndAsync"/>, which notes the rule on <see cref="IRequestTelemetryFeature"/>
    /// for the request line and <c>security.rejections</c> and writes the id's problem body.
    ///
    /// <para>The Warning line is this endpoint's, and it keeps it: the module never logs (#142, Q9).
    /// The line says which of this endpoint's checks refused the request, at the moment it did, which
    /// the request line cannot: that line carries one rule id for the whole request and is written by
    /// another project. Keeping it is this owner's choice, not a contract matter
    /// (contracts/changes/6.4-request-telemetry-feature.md), and <c>AssetTests</c> asserts on its text
    /// and category.</para>
    ///
    /// <para>Named by the glossary's word, as the request gate's helpers are (GLOSSARY.md ->
    /// Rejection); the log message keeps "refused", which the glossary gives the code's log
    /// messages.</para>
    /// </summary>
    private Task RejectAsync(HttpContext context, string rule)
    {
        LogRejected(logger, rule);
        return Rejection.EndAsync(context, rule);
    }

    // The rule id only (contracts/request-gate.md, Rule ids). The path is not logged: it is the
    // client's text, and the telemetry contract's request line already carries the route.
    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "Asset request refused under {Rule}")]
    private static partial void LogRejected(ILogger logger, string rule);
}
