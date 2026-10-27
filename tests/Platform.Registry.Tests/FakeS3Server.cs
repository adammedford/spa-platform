using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Platform.Registry.Tests;

/// <summary>
/// S3's REST semantics for GetObject and PutObject, in memory, behind a real loopback listener on a
/// port the operating system picks. The SDK's own client talks to it over HTTP, so a test proves how
/// the SDK surfaces each status (a 304 and a 412 arrive as exceptions, not as responses) rather than
/// what a hand-written stub assumes it does.
///
/// The statuses are S3's documented ones:
/// GET with a matching <c>If-None-Match</c> is 304, and a missing key is 404 <c>NoSuchKey</c>
/// (https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObject.html); PUT with
/// <c>If-None-Match: *</c> on an existing key, or <c>If-Match</c> with another ETag, is 412, and
/// <c>If-Match</c> on a missing key is 404 (https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes.html).
///
/// <para>It answers a missing key as the caller's grant decides, in one of two modes. With
/// <see cref="ListBucketGranted"/>, as by default, a missing key is 404 <c>NoSuchKey</c>. Without it
/// the caller holds <c>s3:GetObject</c> alone, the gateway's deployed role (P-03), and S3 answers a
/// missing key with 403 <c>AccessDenied</c> where a caller with it gets 404 <c>NoSuchKey</c>
/// (API_GetObject.html, "Permissions").
/// <see cref="DenyRead"/> refuses an existing key with the same 403, the other half of what that
/// answer can mean (change request contracts/changes/issue-381-object-read-refusal.md, item 1).</para>
/// </summary>
internal sealed class FakeS3Server : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly ConcurrentDictionary<string, (byte[] Body, string ETag)> objects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> denied = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<SeenRequest> requests = new();
    private readonly object writes = new();

    private FakeS3Server(WebApplication app) => this.app = app;

    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>Every request, in order, with the conditional headers it carried.</summary>
    public IReadOnlyList<SeenRequest> Requests => [.. requests];

    /// <summary>When set, answers every request with this status and S3 error code instead.</summary>
    public (HttpStatusCode Status, string Code)? FailWith { get; set; }

    /// <summary>
    /// Whether the caller may list the bucket. True: a GET of a missing key is 404 <c>NoSuchKey</c>.
    /// False: it is 403 <c>AccessDenied</c>, as S3 answers a GetObject-only role.
    /// </summary>
    public bool ListBucketGranted { get; set; } = true;

    /// <summary>Refuses every GET of <paramref name="key"/> in <paramref name="bucket"/> with 403 <c>AccessDenied</c>, present or not.</summary>
    public void DenyRead(string bucket, string key) => denied[$"/{bucket}/{key}"] = true;

    public static async Task<FakeS3Server> StartAsync(CancellationToken ct)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        FakeS3Server server = new(app);
        app.Run(server.HandleAsync);
        await app.StartAsync(ct);
        server.BaseAddress = new Uri(app.Urls.First());
        return server;
    }

    /// <summary>
    /// An SDK client pointed at this server, path-style, with anonymous credentials so it signs nothing
    /// and needs no profile, and checksums only where S3 requires them so a body arrives unframed.
    /// </summary>
    public AmazonS3Client Client() => new(new AnonymousAWSCredentials(), new AmazonS3Config
    {
        ServiceURL = BaseAddress.ToString(),
        ForcePathStyle = true,
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    });

    public ValueTask DisposeAsync() => app.DisposeAsync();

    private async Task HandleAsync(HttpContext context)
    {
        HttpRequest request = context.Request;
        string path = Uri.UnescapeDataString(request.Path.Value ?? "");
        requests.Enqueue(new SeenRequest(
            request.Method, path, request.Headers.IfNoneMatch.ToString(), request.Headers.IfMatch.ToString()));

        if (FailWith is { } failure)
        {
            await ErrorAsync(context, failure.Status, failure.Code);
            return;
        }

        if (HttpMethods.IsGet(request.Method))
        {
            await GetAsync(context, path);
        }
        else if (HttpMethods.IsPut(request.Method))
        {
            await PutAsync(context, path);
        }
        else
        {
            await ErrorAsync(context, HttpStatusCode.MethodNotAllowed, "MethodNotAllowed");
        }
    }

    private async Task GetAsync(HttpContext context, string path)
    {
        if (denied.ContainsKey(path))
        {
            await ErrorAsync(context, HttpStatusCode.Forbidden, "AccessDenied");
            return;
        }

        if (!objects.TryGetValue(path, out (byte[] Body, string ETag) stored))
        {
            await (ListBucketGranted
                ? ErrorAsync(context, HttpStatusCode.NotFound, "NoSuchKey")
                : ErrorAsync(context, HttpStatusCode.Forbidden, "AccessDenied"));
            return;
        }

        context.Response.Headers.ETag = stored.ETag;
        if (context.Request.Headers.IfNoneMatch.ToString() == stored.ETag)
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.ContentLength = stored.Body.Length;
        await context.Response.Body.WriteAsync(stored.Body, context.RequestAborted);
    }

    private async Task PutAsync(HttpContext context, string path)
    {
        using MemoryStream buffer = new();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        byte[] body = buffer.ToArray();

        string ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
        string ifMatch = context.Request.Headers.IfMatch.ToString();
        HttpStatusCode? refused = null;
        string code = "";
        string etag = "\"" + Convert.ToHexStringLower(MD5.HashData(body)) + "-" +
            Interlocked.Increment(ref writeCount).ToString(CultureInfo.InvariantCulture) + "\"";

        lock (writes)
        {
            bool exists = objects.TryGetValue(path, out (byte[] Body, string ETag) current);
            if (ifNoneMatch == "*" && exists)
            {
                (refused, code) = (HttpStatusCode.PreconditionFailed, "PreconditionFailed");
            }
            else if (ifMatch.Length > 0 && !exists)
            {
                (refused, code) = (HttpStatusCode.NotFound, "NoSuchKey");
            }
            else if (ifMatch.Length > 0 && ifMatch != current.ETag)
            {
                (refused, code) = (HttpStatusCode.PreconditionFailed, "PreconditionFailed");
            }
            else
            {
                objects[path] = (body, etag);
            }
        }

        if (refused is { } status)
        {
            await ErrorAsync(context, status, code);
            return;
        }

        context.Response.Headers.ETag = etag;
    }

    private long writeCount;

    private static async Task ErrorAsync(HttpContext context, HttpStatusCode status, string code)
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/xml";
        await context.Response.WriteAsync(
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>{code}</Message><RequestId>fake</RequestId></Error>",
            Encoding.UTF8,
            context.RequestAborted);
    }

    public sealed record SeenRequest(string Method, string Path, string IfNoneMatch, string IfMatch);
}
