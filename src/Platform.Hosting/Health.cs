using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Platform.Abstractions;

namespace Platform.Hosting;

/// <summary>
/// <c>UsePlatformHealth</c>'s two answers (contracts/pipeline.md, Health endpoints).
/// </summary>
internal static class Health
{
    /// <summary>
    /// The answer for a health path, or null for any other path, which the caller passes on. The
    /// paths match ordinally: <see cref="PathString"/>'s own equality ignores case
    /// (https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.pathstring.equals), and a
    /// health path answered on <c>/HEALTHZ</c> is a path an SPA could otherwise have served.
    /// </summary>
    public static Task? TryAnswerAsync(HttpContext context, IRegistry registry, GatewayInstance instance)
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
            return ReadyAsync(context, registry.Current, instance);
        }

        return null;
    }

    /// <summary>
    /// 200 with <c>{"revision": N, "instance": "&lt;id&gt;"}</c> once a snapshot is live, 503 with no
    /// body before (contracts/pipeline.md, Health endpoints; change request P2-5). Read on every
    /// request, so the revision reported is the live one, which is what <c>spactl wait</c> waits for. A
    /// document's revision is at least 1 (registry.schema.json), so revision 0 is the empty snapshot the
    /// live registry holds before its first load (<c>LiveRegistry.NoSnapshot</c>, task 2.2).
    ///
    /// The snapshot is taken before the instance's id is awaited, and the id is awaited only on the 200
    /// path: the 503 stays bodiless (P2-5, decision 3), and an id still being read holds up only an
    /// answer that is ready, for at most <see cref="GatewayInstance.MetadataTimeout"/>, once.
    /// </summary>
    private static async Task ReadyAsync(HttpContext context, RegistrySnapshot snapshot, GatewayInstance instance)
    {
        if (snapshot.Revision < 1)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        string id = await instance.IdAsync;

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";

        // Written with the JSON writer, so a harness's id is escaped whatever it holds.
        ArrayBufferWriter<byte> body = new();
        using (Utf8JsonWriter json = new(body))
        {
            json.WriteStartObject();
            json.WriteNumber("revision", snapshot.Revision);
            json.WriteString("instance", id);
            json.WriteEndObject();
        }

        await context.Response.Body.WriteAsync(body.WrittenMemory, context.RequestAborted);
    }
}
