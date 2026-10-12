using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Spike;

/// <summary>
/// Minimal authorization server on its own port: per-client secrets and redirect URIs, codes bound to
/// (client, redirect_uri), one-time codes, rotating refresh tokens. Enough to make cross-client mistakes fail.
/// </summary>
public sealed class FakeIdp
{
    public sealed record Client(string Secret, HashSet<string> RedirectUris);

    public readonly ConcurrentDictionary<string, Client> Clients = new();
    private readonly ConcurrentDictionary<string, (string ClientId, string RedirectUri, string Sub)> _codes = new();
    private readonly ConcurrentDictionary<string, (string ClientId, string Sub)> _refreshTokens = new();
    private int _seq;

    public int AccessTokenLifetimeMs = 3_600_000;
    public int RefreshDelayMs;
    public int TokenRequests;
    public int RefreshGrants;
    public int RejectedTokenRequests;
    public string? LastSecretSeenFor(string clientId) => _lastSecret.TryGetValue(clientId, out var s) ? s : null;
    private readonly ConcurrentDictionary<string, string> _lastSecret = new();

    public string BaseUrl { get; private set; } = "";
    public WebApplication App { get; private set; } = default!;

    public string IssueCodeFor(string clientId, string redirectUri, string sub = "alice")
    {
        var code = "code-" + Guid.NewGuid().ToString("N");
        _codes[code] = (clientId, redirectUri, sub);
        return code;
    }

    public async Task StartAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        var app = b.Build();

        app.MapGet("/authorize", (string client_id, string redirect_uri, string state) =>
        {
            if (!Clients.TryGetValue(client_id, out var c) || !c.RedirectUris.Contains(redirect_uri))
                return Results.BadRequest("unknown client or unregistered redirect_uri");
            var code = IssueCodeFor(client_id, redirect_uri);
            return Results.Redirect($"{redirect_uri}?code={code}&state={Uri.EscapeDataString(state)}");
        });

        app.MapPost("/token", async (HttpRequest req) =>
        {
            Interlocked.Increment(ref TokenRequests);
            var f = await req.ReadFormAsync();
            string clientId = f["client_id"].ToString(), secret = f["client_secret"].ToString();
            _lastSecret[clientId] = secret;
            if (!Clients.TryGetValue(clientId, out var c) || c.Secret != secret) return Reject("invalid_client");

            string sub;
            switch (f["grant_type"].ToString())
            {
                case "authorization_code":
                    if (!_codes.TryRemove(f["code"].ToString(), out var code)) return Reject("invalid_grant");
                    if (code.ClientId != clientId || code.RedirectUri != f["redirect_uri"].ToString()) return Reject("invalid_grant");
                    sub = code.Sub;
                    break;
                case "refresh_token":
                    if (RefreshDelayMs > 0) await Task.Delay(RefreshDelayMs);
                    if (!_refreshTokens.TryRemove(f["refresh_token"].ToString(), out var rt)) return Reject("invalid_grant"); // rotation, no grace
                    if (rt.ClientId != clientId) return Reject("invalid_grant");
                    Interlocked.Increment(ref RefreshGrants);
                    sub = rt.Sub;
                    break;
                default:
                    return Reject("unsupported_grant_type");
            }

            var n = Interlocked.Increment(ref _seq);
            var refresh = "rt~" + Guid.NewGuid().ToString("N");
            _refreshTokens[refresh] = (clientId, sub);
            return Results.Json(new
            {
                access_token = $"at~{clientId}~{sub}~{n}",
                refresh_token = refresh,
                expires_in_ms = AccessTokenLifetimeMs,
                sub,
            });
        });

        await app.StartAsync();
        App = app;
        BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    private IResult Reject(string error)
    {
        Interlocked.Increment(ref RejectedTokenRequests);
        return Results.Json(new { error }, statusCode: 400);
    }
}
