using System.Collections.Immutable;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Platform.Testing.Internal;

namespace Platform.Testing;

/// <summary>
/// An <see cref="EchoUpstream"/> behind a real loopback listener on a port the operating system
/// picks. Task 6.1's harness forwards to this, so the composed suites see headers as they were
/// actually serialized rather than as objects. It starts on the stage host's loopback seam
/// (<see cref="LoopbackStart"/>), so the two listeners in this project start one way.
/// </summary>
public sealed class EchoUpstreamListener : IAsyncDisposable
{
    private readonly WebApplication app;

    private EchoUpstreamListener(WebApplication app, Uri baseAddress)
    {
        this.app = app;
        BaseAddress = baseAddress;
    }

    /// <summary>Where the upstream is listening, e.g. <c>http://127.0.0.1:54321/</c>.</summary>
    public Uri BaseAddress { get; }

    internal static async Task<EchoUpstreamListener> StartAsync(EchoUpstream upstream, CancellationToken ct)
    {
        WebApplicationBuilder builder = LoopbackStart.CreateBuilder();

        WebApplication app = builder.Build();
        app.Run(context => HandleAsync(upstream, context));

        Uri address = await LoopbackStart.StartAsync(app, "the echo upstream", ct);

        return new EchoUpstreamListener(app, address);
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }

    private static async Task HandleAsync(EchoUpstream upstream, HttpContext context)
    {
        ImmutableDictionary<string, ImmutableArray<string>>.Builder headers =
            ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in context.Request.Headers)
        {
            headers[header.Key] = [.. header.Value.Where(value => value is not null).Select(value => value!)];
        }

        using StreamReader reader = new(context.Request.Body, Encoding.UTF8);
        string body = await reader.ReadToEndAsync(context.RequestAborted);

        EchoedRequest received = upstream.Record(
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            context.Request.QueryString.Value ?? string.Empty,
            headers.ToImmutable(),
            body);

        EchoResponse arranged = upstream.ResponseFor(received);

        if (arranged.Body.Length == 0 && arranged.Status == HttpStatusCode.OK)
        {
            await Results.Json(received).ExecuteAsync(context);
            return;
        }

        context.Response.StatusCode = (int)arranged.Status;
        context.Response.ContentType = arranged.ContentType;
        await context.Response.WriteAsync(arranged.Body, context.RequestAborted);
    }
}
