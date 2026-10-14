using System.Globalization;
using Microsoft.AspNetCore.Http;
using Platform.Abstractions;

namespace Platform.Hosting;

/// <summary>
/// Stage 3's two answers (contracts/pipeline.md, Health endpoints).
/// </summary>
internal static class Health
{
    /// <summary>
    /// The answer for a health path, or null for any other path, which the caller passes on. The
    /// paths match ordinally: <see cref="PathString"/>'s own equality ignores case
    /// (https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.pathstring.equals), and a
    /// health path answered on <c>/HEALTHZ</c> is a path an SPA could otherwise have served.
    /// </summary>
    public static Task? TryAnswerAsync(HttpContext context, IRegistry registry)
    {
        string? path = context.Request.Path.Value;

        if (string.Equals(path, "/healthz", StringComparison.Ordinal))
        {
            // 200 while the process runs, and nothing else: a gateway that cannot read its registry
            // keeps serving the last snapshot, so it is alive even when it is not ready.
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        }

        if (string.Equals(path, "/readyz", StringComparison.Ordinal))
        {
            return ReadyAsync(context, registry.Current);
        }

        return null;
    }

    /// <summary>
    /// 200 with <c>{"revision": N}</c> once a snapshot is live, 503 before. Read on every request, so
    /// the revision reported is the live one, which is what <c>spactl wait</c> waits for. A document's
    /// revision is at least 1 (registry.schema.json), so revision 0 is the empty snapshot the live
    /// registry holds before its first load (<c>LiveRegistry.NoSnapshot</c>, task 2.2).
    /// </summary>
    private static Task ReadyAsync(HttpContext context, RegistrySnapshot snapshot)
    {
        if (snapshot.Revision < 1)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(
            "{\"revision\":" + snapshot.Revision.ToString(CultureInfo.InvariantCulture) + "}", context.RequestAborted);
    }
}
