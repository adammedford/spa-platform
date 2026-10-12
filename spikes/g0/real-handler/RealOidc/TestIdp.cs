using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Spike;

/// <summary>
/// Hermetic OpenID Connect provider for the review experiment: discovery, JWKS, authorization code with PKCE
/// (S256 required), RS256 ID tokens carrying the request nonce, client-bound single-use codes, RP-initiated
/// logout. Enough for the real OpenIdConnectHandler to run its full validation path.
/// </summary>
public sealed class TestIdp
{
    public sealed record Client(string Secret, HashSet<string> RedirectUris, HashSet<string> PostLogoutUris);
    private sealed record CodeGrant(string ClientId, string RedirectUri, string Sub, string? Nonce, string Challenge, string Scope, string? Resource);

    public readonly ConcurrentDictionary<string, Client> Clients = new();
    private readonly ConcurrentDictionary<string, CodeGrant> _codes = new();
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly string _kid = "review-key-1";
    private int _seq;

    public int DiscoveryFetches, JwksFetches, TokenRequests, RejectedTokenRequests, PkceVerified, EndSessions;
    public string? LastAuthorizeQuery;
    public string? LastSecretSeenFor(string clientId) => _lastSecret.TryGetValue(clientId, out var s) ? s : null;
    private readonly ConcurrentDictionary<string, string> _lastSecret = new();
    public string? LastTokenError;

    public string BaseUrl { get; private set; } = "";
    public WebApplication App { get; private set; } = default!;

    public async Task StartAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        var app = b.Build();

        app.MapGet("/.well-known/openid-configuration", () =>
        {
            Interlocked.Increment(ref DiscoveryFetches);
            return Results.Json(new Dictionary<string, object>
            {
                ["issuer"] = BaseUrl,
                ["authorization_endpoint"] = BaseUrl + "/authorize",
                ["token_endpoint"] = BaseUrl + "/token",
                ["jwks_uri"] = BaseUrl + "/jwks",
                ["end_session_endpoint"] = BaseUrl + "/endsession",
                ["response_types_supported"] = new[] { "code" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["code_challenge_methods_supported"] = new[] { "S256" },
            });
        });

        app.MapGet("/jwks", () =>
        {
            Interlocked.Increment(ref JwksFetches);
            var p = _rsa.ExportParameters(false);
            return Results.Json(new
            {
                keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = _kid, n = WebEncoders.Base64UrlEncode(p.Modulus!), e = WebEncoders.Base64UrlEncode(p.Exponent!) } }
            });
        });

        // The "user" is whoever the test says is typing at the login page: ?login_hint=mallory, default alice.
        app.MapGet("/authorize", (HttpContext ctx) =>
        {
            var q = ctx.Request.Query;
            LastAuthorizeQuery = ctx.Request.QueryString.Value;
            string clientId = q["client_id"].ToString(), redirectUri = q["redirect_uri"].ToString(), state = q["state"].ToString();
            if (!Clients.TryGetValue(clientId, out var c) || !c.RedirectUris.Contains(redirectUri))
                return Results.BadRequest("unknown client or unregistered redirect_uri");
            if (q["response_type"] != "code") return Results.BadRequest("only response_type=code");
            if (q["code_challenge_method"] != "S256" || string.IsNullOrEmpty(q["code_challenge"])) return Results.BadRequest("PKCE S256 required");

            var sub = string.IsNullOrEmpty(q["login_hint"]) ? "alice" : q["login_hint"].ToString();
            var code = "code-" + Guid.NewGuid().ToString("N");
            _codes[code] = new CodeGrant(clientId, redirectUri, sub, q["nonce"], q["code_challenge"].ToString(), q["scope"].ToString(), string.IsNullOrEmpty(q["resource"]) ? null : q["resource"].ToString());

            if (q["response_mode"] == "form_post")
            {
                // What a real provider does for form_post: an auto-submitting cross-site POST. The test posts it by hand.
                return Results.Content($"<form method=\"post\" action=\"{redirectUri}\"><input name=\"code\" value=\"{code}\"/><input name=\"state\" value=\"{System.Net.WebUtility.HtmlEncode(state)}\"/></form>", "text/html");
            }
            return Results.Redirect($"{redirectUri}?code={code}&state={Uri.EscapeDataString(state)}");
        });

        app.MapPost("/token", async (HttpRequest req) =>
        {
            Interlocked.Increment(ref TokenRequests);
            var f = await req.ReadFormAsync();
            string clientId = f["client_id"].ToString(), secret = f["client_secret"].ToString();
            _lastSecret[clientId] = secret;
            if (!Clients.TryGetValue(clientId, out var c) || c.Secret != secret) return Reject("invalid_client");
            if (f["grant_type"] != "authorization_code") return Reject("unsupported_grant_type");
            if (!_codes.TryRemove(f["code"].ToString(), out var code)) return Reject("invalid_grant");
            if (code.ClientId != clientId || code.RedirectUri != f["redirect_uri"].ToString()) return Reject("invalid_grant");

            var verifier = f["code_verifier"].ToString();
            if (string.IsNullOrEmpty(verifier) || WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != code.Challenge) return Reject("invalid_grant");
            Interlocked.Increment(ref PkceVerified);

            var n = Interlocked.Increment(ref _seq);
            var accessToken = $"at~{clientId}~{code.Sub}~{n}~aud={code.Resource ?? "-"}";
            var atHash = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)).AsSpan(0, 16));
            var claims = new Dictionary<string, object> { ["sub"] = code.Sub, ["at_hash"] = atHash, ["name"] = code.Sub + " (display)" };
            if (!string.IsNullOrEmpty(code.Nonce)) claims["nonce"] = code.Nonce;
            var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = BaseUrl,
                Audience = clientId,
                Claims = claims,
                IssuedAt = DateTime.UtcNow,
                NotBefore = DateTime.UtcNow.AddSeconds(-5),
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = _kid }, SecurityAlgorithms.RsaSha256),
            });
            return Results.Json(new
            {
                access_token = accessToken,
                token_type = "Bearer",
                expires_in = 300,
                refresh_token = "rt~" + Guid.NewGuid().ToString("N"),
                id_token = idToken,
            });
        });

        app.MapGet("/endsession", (HttpContext ctx) =>
        {
            Interlocked.Increment(ref EndSessions);
            var q = ctx.Request.Query;
            var back = q["post_logout_redirect_uri"].ToString();
            if (!Clients.Values.Any(c => c.PostLogoutUris.Contains(back))) return Results.BadRequest("unregistered post_logout_redirect_uri");
            return Results.Redirect(string.IsNullOrEmpty(q["state"]) ? back : $"{back}?state={Uri.EscapeDataString(q["state"].ToString())}");
        });

        await app.StartAsync();
        App = app;
        BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    private IResult Reject(string error)
    {
        Interlocked.Increment(ref RejectedTokenRequests);
        LastTokenError = error;
        return Results.Json(new { error }, statusCode: 400);
    }
}
