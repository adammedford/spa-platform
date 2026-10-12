using System.Collections.Immutable;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Platform.Testing;

/// <summary>
/// An <see cref="EchoUpstream"/> behind a real loopback listener on a port the operating system
/// picks. Task 6.1's harness forwards to this, so the composed suites see headers as they were
/// actually serialized rather than as objects.
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
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        WebApplication app = builder.Build();
        app.Run(context => HandleAsync(upstream, context));

        await app.StartAsync(ct);

        string address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("the echo upstream started without an address.");

        return new EchoUpstreamListener(app, new Uri(address));
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
