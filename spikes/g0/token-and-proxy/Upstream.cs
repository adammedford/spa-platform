using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Spike;

/// <summary>
/// Stands in for an API behind the gateway. It answers every request with what it received, so a check can
/// see exactly what YARP forwarded. A few paths misbehave on purpose.
/// </summary>
public sealed class EchoApi(string name)
{
    public sealed record Seen(string Method, string Path, string Query, Dictionary<string, string> Headers, string Body);

    private readonly ConcurrentQueue<Seen> _seen = new();

    public string Name => name;
    public IReadOnlyList<Seen> Requests => _seen.ToArray();
    public int Count => _seen.Count;
    public string BaseUrl { get; private set; } = "";
    public WebApplication App { get; private set; } = default!;

    public async Task StartAsync()
    {
        var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        b.Configuration["ForwardedHeaders_Enabled"] = "false";   // this API must record headers as they arrive, whatever the machine's environment says
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        var app = b.Build();

        app.Run(async ctx =>
        {
            var req = ctx.Request;
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            var headers = req.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            _seen.Enqueue(new Seen(req.Method, req.Path.Value ?? "", req.QueryString.Value ?? "", headers, body));

            switch (req.Path.Value)
            {
                // What a careless or hostile upstream might send back.
                case "/headers":
                    ctx.Response.Headers.Append("Set-Cookie", "upstream-session=1; Path=/");
                    ctx.Response.Headers.Append("Set-Cookie", "__Host-spa-session=forged; Path=/; Secure");
                    ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
                    ctx.Response.Headers["Access-Control-Allow-Credentials"] = "true";
                    ctx.Response.Headers["Content-Security-Policy"] = "default-src *";
                    ctx.Response.Headers["Cache-Control"] = "max-age=60";
                    ctx.Response.Headers["X-Upstream"] = name;
                    if (int.TryParse(req.Query["status"], out var status)) ctx.Response.StatusCode = status;
                    break;
                case "/redirect":
                    ctx.Response.Redirect("https://elsewhere.test/landing");
                    return;
                // Silent before it answers at all.
                case "/slow":
                    await Task.Delay(int.Parse(req.Query["ms"].ToString()), ctx.RequestAborted);
                    break;
                // Sends its headers, then goes silent.
                case "/stall":
                    await ctx.Response.StartAsync();
                    await ctx.Response.Body.FlushAsync();
                    await Task.Delay(int.Parse(req.Query["ms"].ToString()), ctx.RequestAborted);
                    return;
                // Never silent for long, never finished quickly: one byte every <gap> milliseconds.
                case "/drip":
                    ctx.Response.ContentType = "text/plain";
                    for (var i = 0; i < int.Parse(req.Query["count"].ToString()); i++)
                    {
                        await ctx.Response.WriteAsync(".", ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        await Task.Delay(int.Parse(req.Query["gap"].ToString()), ctx.RequestAborted);
                    }
                    return;
            }

            await ctx.Response.WriteAsJsonAsync(new { api = name, method = req.Method, path = req.Path.Value, query = req.QueryString.Value, headers, body });
        });

        await app.StartAsync();
        App = app;
        BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }
}
