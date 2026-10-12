using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Spike;

/// <summary>
/// Hermetic OpenID Connect provider for G0 items 4 and 5. It is the provider of the real-handler harness
/// (discovery, JWKS, authorization code with PKCE, signed ID tokens, end session) plus what token management
/// needs: the refresh grant with client-bound, rotating refresh tokens, a rotation grace window like Cognito's,
/// revocation, client authentication by Basic header or form body, and a log of every grant.
/// Tokens are readable test strings, not secrets: at~{client}~{sub}~{n}~aud={resource} and rt~{client}~{n}~{random}.
/// </summary>
public sealed class RotatingIdp
{
    public sealed record Client(string Secret, HashSet<string> RedirectUris, HashSet<string> PostLogoutUris);

    /// <summary>One request to the token endpoint. Auth is "basic", "post" or "none".</summary>
    public sealed record Grant(int Seq, string Type, string ClientId, string Auth, string Outcome,
        string? RefreshTokenUsed, string? RefreshTokenIssued, string? AccessToken, bool SentResource, bool SentScope, string UserAgent)
    {
        public bool Ok => Outcome == "ok";
    }

    public sealed record Revocation(string ClientId, string Auth, string Token, string? Hint, bool Known);

    private sealed record CodeGrant(string ClientId, string RedirectUri, string Sub, string? Nonce, string Challenge, string Scope, string? Resource);

    private sealed class RefreshState
    {
        public required string ClientId, Sub, Scope;
        public string? Resource;
        public DateTimeOffset? RotatedOutAt;
        public bool Revoked;
    }

    // ---- knobs the checks turn ----
    /// <summary>Lifetime, in seconds, of every access token issued from now on. The library refreshes a token with under a minute left.</summary>
    public int AccessTokenLifetime = 300;
    /// <summary>How long a rotated-out refresh token still works. Cognito allows 0 to 60 seconds; the plan configures 10.</summary>
    public int RefreshGraceSeconds;
    /// <summary>Delay before the refresh grant is looked at, so that parallel callers overlap. An outage waits this long too.</summary>
    public int RefreshDelayMs;
    /// <summary>Delay between carrying out a refresh grant and answering it: the refresh token is already rotated, and the client does not know yet.</summary>
    public int RefreshAnswerDelayMs;
    public bool RotateRefreshTokens = true;
    /// <summary>Extra characters on every refresh token issued from now on, to make one longer than a client library accepts.</summary>
    public int RefreshTokenPadding;
    /// <summary>OpenID Connect lets a refresh response leave the ID token out. Cognito sends one.</summary>
    public bool IdTokenOnRefresh = true;
    /// <summary>Whether discovery lists revocation_endpoint. Set before the first gateway reads discovery.</summary>
    public bool AdvertiseRevocation = true;
    /// <summary>When set, the refresh grant answers with this HTTP status and does nothing else: an outage, not a refusal.</summary>
    public int? RefreshOutageStatus;

    public readonly ConcurrentDictionary<string, Client> Clients = new();
    private readonly ConcurrentDictionary<string, CodeGrant> _codes = new();
    private readonly ConcurrentDictionary<string, RefreshState> _refresh = new();
    private readonly ConcurrentQueue<Grant> _grants = new();
    private readonly ConcurrentQueue<Revocation> _revocations = new();
    private readonly RSA _rsa = RSA.Create(2048);
    private const string Kid = "g0-key-1";
    private int _seq;

    public int DiscoveryFetches, JwksFetches, EndSessions;

    public IReadOnlyList<Grant> Grants => _grants.ToArray();
    public IReadOnlyList<Grant> RefreshGrants => _grants.Where(g => g.Type == "refresh_token").ToArray();
    public IReadOnlyList<Revocation> Revocations => _revocations.ToArray();

    public string BaseUrl { get; private set; } = "";
    public WebApplication App { get; private set; } = default!;

    /// <summary>Short name for a token in the output: "at#12 client-1" or "rt#7 client-1". Never the whole value.</summary>
    public static string Label(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "(none)";
        var p = token.Split('~');
        return p[0] switch
        {
            "at" when p.Length >= 4 => $"at#{p[3]} {p[1]}",
            "rt" when p.Length >= 3 => $"rt#{p[2]} {p[1]}",
            _ => "(unrecognised)",
        };
    }

    public static string ClientOf(string? token) => token?.Split('~') is { Length: >= 2 } p ? p[1] : "";
    public static string AudienceOf(string? accessToken) => accessToken is not null && accessToken.Contains("~aud=") ? accessToken[(accessToken.IndexOf("~aud=", StringComparison.Ordinal) + 5)..] : "";

    /// <summary>Whether the provider would still accept this refresh token: issued, not revoked, not rotated out.</summary>
    public bool IsRedeemable(string? refreshToken)
        => refreshToken is not null && _refresh.TryGetValue(refreshToken, out var st) && !st.Revoked && st.RotatedOutAt is null;

    /// <summary>What an administrator's "sign this user out everywhere" does at the provider.</summary>
    public int RevokeAllRefreshTokens(string clientId)
    {
        var n = 0;
        foreach (var st in _refresh.Values.Where(s => s.ClientId == clientId && !s.Revoked)) { st.Revoked = true; n++; }
        return n;
    }

    public async Task StartAsync()
    {
        var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        b.Configuration["ForwardedHeaders_Enabled"] = "false";   // whatever ASPNETCORE_FORWARDEDHEADERS_ENABLED says on this machine
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        var app = b.Build();

        app.MapGet("/.well-known/openid-configuration", () =>
        {
            Interlocked.Increment(ref DiscoveryFetches);
            var document = new Dictionary<string, object>
            {
                ["issuer"] = BaseUrl,
                ["authorization_endpoint"] = BaseUrl + "/authorize",
                ["token_endpoint"] = BaseUrl + "/token",
                ["jwks_uri"] = BaseUrl + "/jwks",
                ["end_session_endpoint"] = BaseUrl + "/endsession",
                ["response_types_supported"] = new[] { "code" },
                ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
                ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["code_challenge_methods_supported"] = new[] { "S256" },
            };
            if (AdvertiseRevocation) document["revocation_endpoint"] = BaseUrl + "/revoke";
            return Results.Json(document);
        });

        app.MapGet("/jwks", () =>
        {
            Interlocked.Increment(ref JwksFetches);
            var p = _rsa.ExportParameters(false);
            return Results.Json(new
            {
                keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = Kid, n = WebEncoders.Base64UrlEncode(p.Modulus!), e = WebEncoders.Base64UrlEncode(p.Exponent!) } }
            });
        });

        // The "user" is whoever the test says is typing at the login page: ?login_hint=bob, default alice.
        app.MapGet("/authorize", (HttpContext ctx) =>
        {
            var q = ctx.Request.Query;
            string clientId = q["client_id"].ToString(), redirectUri = q["redirect_uri"].ToString(), state = q["state"].ToString();
            if (!Clients.TryGetValue(clientId, out var c) || !c.RedirectUris.Contains(redirectUri))
                return Results.BadRequest("unknown client or unregistered redirect_uri");
            if (q["response_type"] != "code") return Results.BadRequest("only response_type=code");
            if (q["code_challenge_method"] != "S256" || string.IsNullOrEmpty(q["code_challenge"])) return Results.BadRequest("PKCE S256 required");

            var sub = string.IsNullOrEmpty(q["login_hint"]) ? "alice" : q["login_hint"].ToString();
            var code = "code-" + Guid.NewGuid().ToString("N");
            _codes[code] = new CodeGrant(clientId, redirectUri, sub, q["nonce"], q["code_challenge"].ToString(), q["scope"].ToString(),
                string.IsNullOrEmpty(q["resource"]) ? null : q["resource"].ToString());
            return Results.Redirect($"{redirectUri}?code={code}&state={Uri.EscapeDataString(state)}");
        });

        app.MapPost("/token", async (HttpRequest req) =>
        {
            var f = await req.ReadFormAsync();
            var grantType = f["grant_type"].ToString();
            var (clientId, secret, auth) = ReadClient(req, f);
            bool sentResource = !string.IsNullOrEmpty(f["resource"]), sentScope = !string.IsNullOrEmpty(f["scope"]);
            var usedRefresh = grantType == "refresh_token" ? f["refresh_token"].ToString() : null;
            var userAgent = req.Headers.UserAgent.ToString();

            IResult Reject(string error, int status = 400)
            {
                Log(new Grant(0, grantType, clientId ?? "", auth, error, usedRefresh, null, null, sentResource, sentScope, userAgent));
                return Results.Json(new { error }, statusCode: status);
            }

            if (clientId is null || !Clients.TryGetValue(clientId, out var c) || c.Secret != secret) return Reject("invalid_client", 401);

            if (grantType == "authorization_code")
            {
                if (!_codes.TryRemove(f["code"].ToString(), out var code)) return Reject("invalid_grant");
                if (code.ClientId != clientId || code.RedirectUri != f["redirect_uri"].ToString()) return Reject("invalid_grant");
                var verifier = f["code_verifier"].ToString();
                if (string.IsNullOrEmpty(verifier) || WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != code.Challenge) return Reject("invalid_grant");
                return Issue(grantType, clientId, auth, code.Sub, code.Scope, code.Resource, code.Nonce, null, sentResource, sentScope, userAgent, newRefreshToken: true, idToken: true);
            }

            if (grantType == "refresh_token")
            {
                if (RefreshDelayMs > 0) await Task.Delay(RefreshDelayMs);
                if (RefreshOutageStatus is { } outage)
                {
                    Log(new Grant(0, grantType, clientId, auth, "http_" + outage, usedRefresh, null, null, sentResource, sentScope, userAgent));
                    return Results.StatusCode(outage);
                }
                // A refresh token belongs to the client it was issued to. Rotated-out tokens work only inside the grace window.
                if (!_refresh.TryGetValue(usedRefresh!, out var st) || st.ClientId != clientId || st.Revoked) return Reject("invalid_grant");
                lock (st)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (st.RotatedOutAt is { } rotatedAt && (now - rotatedAt).TotalSeconds > RefreshGraceSeconds) return Reject("invalid_grant");
                    if (RotateRefreshTokens) st.RotatedOutAt ??= now;
                }
                // The audience stays the one bound at login unless the request names another. A real provider may differ: G0 item 2 checks Cognito.
                var resource = sentResource ? f["resource"].ToString() : st.Resource;
                var answer = Issue(grantType, clientId, auth, st.Sub, st.Scope, resource, null, usedRefresh, sentResource, sentScope, userAgent, newRefreshToken: RotateRefreshTokens, idToken: IdTokenOnRefresh);
                if (RefreshAnswerDelayMs > 0) await Task.Delay(RefreshAnswerDelayMs);
                return answer;
            }

            return Reject("unsupported_grant_type");
        });

        // RFC 7009. Always 200 for an authenticated client, whether or not the token is known.
        app.MapPost("/revoke", async (HttpRequest req) =>
        {
            var f = await req.ReadFormAsync();
            var (clientId, secret, auth) = ReadClient(req, f);
            if (clientId is null || !Clients.TryGetValue(clientId, out var c) || c.Secret != secret) return Results.Json(new { error = "invalid_client" }, statusCode: 401);
            var token = f["token"].ToString();
            var known = _refresh.TryGetValue(token, out var st) && st.ClientId == clientId;
            if (known) st!.Revoked = true;
            _revocations.Enqueue(new Revocation(clientId, auth, token, string.IsNullOrEmpty(f["token_type_hint"]) ? null : f["token_type_hint"].ToString(), known));
            return Results.Ok();
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

    private IResult Issue(string grantType, string clientId, string auth, string sub, string scope, string? resource, string? nonce,
        string? usedRefresh, bool sentResource, bool sentScope, string userAgent, bool newRefreshToken, bool idToken)
    {
        var n = Interlocked.Increment(ref _seq);
        var accessToken = $"at~{clientId}~{sub}~{n}~aud={resource ?? "-"}";
        string? refreshToken = null;
        if (newRefreshToken)
        {
            refreshToken = $"rt~{clientId}~{n}~{Guid.NewGuid():N}{new string('p', RefreshTokenPadding)}";
            _refresh[refreshToken] = new RefreshState { ClientId = clientId, Sub = sub, Scope = scope, Resource = resource };
        }

        var claims = new Dictionary<string, object>
        {
            ["sub"] = sub,
            ["name"] = sub + " (display)",
            ["at_hash"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)).AsSpan(0, 16)),
        };
        if (!string.IsNullOrEmpty(nonce)) claims["nonce"] = nonce;
        var signedIdToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = BaseUrl,
            Audience = clientId,
            Claims = claims,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddSeconds(-5),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = Kid }, SecurityAlgorithms.RsaSha256),
        });

        Log(new Grant(0, grantType, clientId, auth, "ok", usedRefresh, refreshToken, accessToken, sentResource, sentScope, userAgent));
        var body = new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = AccessTokenLifetime,
        };
        if (idToken) body["id_token"] = signedIdToken;
        if (refreshToken is not null) body["refresh_token"] = refreshToken;
        return Results.Json(body);
    }

    private int _grantSeq;
    private void Log(Grant g) => _grants.Enqueue(g with { Seq = Interlocked.Increment(ref _grantSeq) });

    /// <summary>client_secret_basic as RFC 6749 section 2.3.1 defines it (form-encoded, then Base64), or client_secret_post.</summary>
    private static (string? Id, string? Secret, string Auth) ReadClient(HttpRequest req, IFormCollection f)
    {
        var header = req.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var raw = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var i = raw.IndexOf(':');
                if (i > 0) return (Uri.UnescapeDataString(raw[..i].Replace('+', ' ')), Uri.UnescapeDataString(raw[(i + 1)..].Replace('+', ' ')), "basic");
            }
            catch (FormatException) { }
            return (null, null, "basic");
        }
        if (!string.IsNullOrEmpty(f["client_id"])) return (f["client_id"].ToString(), f["client_secret"].ToString(), "post");
        return (null, null, "none");
    }
}
