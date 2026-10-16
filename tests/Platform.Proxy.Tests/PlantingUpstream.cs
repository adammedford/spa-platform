using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Platform.Proxy.Tests;

/// <summary>
/// An upstream that answers with every response header the gateway must not pass to the browser:
/// a cookie, cross-origin grants and its own content security policy, on whatever status a test sets.
/// <c>Platform.Testing.EchoResponse</c> has a status and a body but no headers, so this lives here.
/// </summary>
public sealed class PlantingUpstream : IAsyncDisposable
{
    public const string PlantedCookie = "upstream-session=planted-cookie; Path=/";
    public const string PlantedOrigin = "https://evil.test";
    public const string PlantedPolicy = "default-src * 'unsafe-inline'";
    public const string Body = "planted-body";

    private readonly WebApplication app;

    private PlantingUpstream(WebApplication app, Uri baseAddress)
    {
        this.app = app;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    /// <summary>The status the next answer carries.</summary>
    public int Status { get; set; } = StatusCodes.Status200OK;

    public static async Task<PlantingUpstream> StartAsync(CancellationToken ct)
    {
        WebApplication web = Loopback.Builder().Build();
        PlantingUpstream? upstream = null;

        web.Run(async context =>
        {
            context.Response.StatusCode = upstream!.Status;
            context.Response.Headers.Append("Set-Cookie", PlantedCookie);
            context.Response.Headers.Append("Set-Cookie", "second=also-planted; Path=/");
            context.Response.Headers["Access-Control-Allow-Origin"] = PlantedOrigin;
            context.Response.Headers["Access-Control-Allow-Credentials"] = "true";
            context.Response.Headers["Access-Control-Expose-Headers"] = "X-Upstream-Marker";
            context.Response.Headers.ContentSecurityPolicy = PlantedPolicy;
            context.Response.Headers["X-Upstream-Marker"] = "control";
            await context.Response.WriteAsync(Body, context.RequestAborted);
        });

        await web.StartAsync(ct);
        upstream = new PlantingUpstream(web, Loopback.AddressOf(web, "planting upstream"));
        return upstream;
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
