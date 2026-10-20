using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TestIdp;

/// <summary>
/// The test identity provider: an OpenID Connect provider for the hermetic tier only, served over loopback
/// in the test process. It offers discovery, JWKS, the authorization code grant with PKCE required, the
/// refresh grant with client-bound rotating refresh tokens and a grace window, revocation, end session in
/// the standard and the Cognito style, <c>resource</c> mapped to <c>aud</c>, and the fault switches on
/// <see cref="Faults"/>.
/// <para>
/// It starts from the provider of the G0 token-and-proxy harness (<c>spikes/g0/token-and-proxy/Idp.cs</c>,
/// checks <c>T-</c> in <c>docs/plan/07-evidence.md</c>). This is the one place the plan writes protocol
/// code, because a real provider will not rotate a signing key, refuse a refresh or delay an answer on
/// demand. The exception is bounded: it is test-only, signing and key handling go through
/// Microsoft.IdentityModel, it references no <c>Platform.*</c> project, and check A-01 fails the build if
/// the gateway references it.
/// </para>
/// <para>
/// The provider writes no log lines. Every request it answers is recorded in memory, for a test to assert
/// on, and the <c>ToString</c> of each record names tokens by label only (see <see cref="Label"/>).
/// </para>
/// </summary>
public sealed class TestIdentityProvider : IAsyncDisposable
{
    private sealed record AuthorizationCode(string ClientId, string RedirectUri, string Subject, string? Nonce, string Challenge, string Scope, string? Resource);

    private sealed class RefreshState
    {
        public required string FamilyId { get; init; }
        public required string ClientId { get; init; }
        public required string Subject { get; init; }
        public required string Scope { get; init; }
        public string? Resource { get; init; }
        public DateTimeOffset? RotatedOutAt { get; set; }
        public bool Revoked { get; set; }
    }

    private readonly ConcurrentDictionary<string, TestIdpClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AuthorizationCode> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RefreshState> _refresh = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Authorization> _authorizations = new();
    private readonly ConcurrentQueue<TokenGrant> _grants = new();
    private readonly ConcurrentQueue<Revocation> _revocations = new();
    private readonly ConcurrentQueue<Logout> _logouts = new();
    private readonly ConcurrentQueue<string> _issuedAccessTokens = new();
    private readonly ConcurrentQueue<string> _issuedRefreshTokens = new();
    private readonly SigningKeyRing _keys = new();
    private readonly TimeProvider _clock;
    private WebApplication? _app;
    private int _seq, _authSeq, _grantSeq, _revokeSeq, _logoutSeq, _discoveryFetches, _jwksFetches;

    /// <param name="clock">The clock the grace window, token lifetimes and the injected delays run on.
    /// A test that needs to move time past the grace window passes a fake one.</param>
    public TestIdentityProvider(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>The switches that inject faults. Settable while the provider is running.</summary>
    public TestIdpFaults Faults { get; } = new();

    /// <summary>Which logout the discovery document advertises. Both endpoints are always served.</summary>
    public EndSessionStyle EndSessionStyle { get; set; } = EndSessionStyle.Standard;

    /// <summary>The subject a login gets when the authorize request sends no <c>login_hint</c>.</summary>
    public string DefaultSubject { get; set; } = "alice";

    /// <summary>The provider's origin, known once it has started.</summary>
    public string BaseUrl { get; private set; } = "";

    /// <summary>What the provider puts in <c>iss</c>. <see cref="TestIdpFaults.IssuerOverride"/> changes
    /// this for issued tokens only; the discovery document keeps naming <see cref="BaseUrl"/>.</summary>
    public string Issuer => Faults.IssuerOverride ?? BaseUrl;

    /// <summary>Where the discovery document is.</summary>
    public string DiscoveryUri => BaseUrl + "/.well-known/openid-configuration";

    /// <summary>Where the signing keys are.</summary>
    public string JwksUri => BaseUrl + "/jwks";

    /// <summary>Where a login starts.</summary>
    public string AuthorizeEndpoint => BaseUrl + "/authorize";

    /// <summary>Where both grants are carried out.</summary>
    public string TokenEndpoint => BaseUrl + "/token";

    /// <summary>Where a refresh token is revoked.</summary>
    public string RevocationEndpoint => BaseUrl + "/revoke";

    /// <summary>Standard RP-initiated logout.</summary>
    public string EndSessionEndpoint => BaseUrl + "/endsession";

    /// <summary>Cognito-style logout, which takes <c>client_id</c> and <c>logout_uri</c>.</summary>
    public string CognitoLogoutEndpoint => BaseUrl + "/logout";

    /// <summary>The key id every token is signed with.</summary>
    public string CurrentKeyId => _keys.CurrentKeyId;

    /// <summary>The registered clients.</summary>
    public IReadOnlyDictionary<string, TestIdpClient> Clients => _clients;

    /// <summary>Every answered authorize request, oldest first.</summary>
    public IReadOnlyList<Authorization> Authorizations => [.. _authorizations];

    /// <summary>Every answered token request, oldest first.</summary>
    public IReadOnlyList<TokenGrant> Grants => [.. _grants];

    /// <summary>The refresh grants among <see cref="Grants"/>.</summary>
    public IReadOnlyList<TokenGrant> RefreshGrants => [.. _grants.Where(g => g.GrantType == "refresh_token")];

    /// <summary>Every answered revocation request, oldest first.</summary>
    public IReadOnlyList<Revocation> Revocations => [.. _revocations];

    /// <summary>Every answered logout request, of either style, oldest first.</summary>
    public IReadOnlyList<Logout> Logouts => [.. _logouts];

    /// <summary>How many discovery documents were served. One per client is the expectation (R-A7).</summary>
    public int DiscoveryFetches => Volatile.Read(ref _discoveryFetches);

    /// <summary>How many key sets were served.</summary>
    public int JwksFetches => Volatile.Read(ref _jwksFetches);

    /// <summary>How many logout requests were accepted, of either style.</summary>
    public int EndSessions => _logouts.Count(l => l.Ok);

    /// <summary>Every access token the provider has issued. F-05 and N-16 search a dump for these.</summary>
    public IReadOnlyList<string> IssuedAccessTokens => [.. _issuedAccessTokens];

    /// <summary>Every refresh token the provider has issued.</summary>
    public IReadOnlyList<string> IssuedRefreshTokens => [.. _issuedRefreshTokens];

    /// <summary>Registers a client. Safe while the provider is running, which is what F-07 onboards into.</summary>
    public void AddClient(TestIdpClient client) => _clients[client.ClientId] = client;

    /// <summary>Registers a client with one redirect URI and one post-logout URI.</summary>
    public TestIdpClient AddClient(string clientId, string secret, string redirectUri, string postLogoutUri)
    {
        var client = TestIdpClient.Create(clientId, secret, redirectUri, postLogoutUri);
        AddClient(client);
        return client;
    }

    /// <summary>
    /// Registers a client with one redirect URI and several post-logout URIs. Each identity profile (ADR-0011)
    /// returns to a different one: the standard profile to the signed-out callback (R-C6), the Cognito profile to
    /// the app's root (R-C7), so a client that serves both registers both here, once.
    /// </summary>
    public TestIdpClient AddClient(string clientId, string secret, string redirectUri, IEnumerable<string> postLogoutUris)
    {
        var client = TestIdpClient.Create(clientId, secret, redirectUri, postLogoutUris);
        AddClient(client);
        return client;
    }

    /// <summary>Removes a client. Its refresh tokens stop being redeemable.</summary>
    public bool RemoveClient(string clientId) => _clients.TryRemove(clientId, out _);

    /// <summary>Rotates a client's secret, as F-09 does at runtime.</summary>
    public void SetClientSecret(string clientId, string secret)
    {
        if (_clients.TryGetValue(clientId, out var client)) _clients[clientId] = client with { Secret = secret };
    }

    /// <summary>Generates a new signing key, makes it current and returns its key id. Keys retired this way
    /// stay in JWKS while <see cref="TestIdpFaults.PublishRetiredKeys"/> is on.</summary>
    public string RotateSigningKey() => _keys.Rotate();

    /// <summary>Whether the provider would still redeem this refresh token: issued, not revoked, and either
    /// current or rotated out inside the grace window.</summary>
    public bool IsRedeemable(string? refreshToken)
    {
        if (refreshToken is null || !_refresh.TryGetValue(refreshToken, out var state) || state.Revoked) return false;
        return state.RotatedOutAt is not { } rotatedAt || _clock.GetUtcNow() - rotatedAt <= Faults.RefreshRotationGrace;
    }

    /// <summary>Whether this refresh token has been rotated out by a later refresh.</summary>
    public bool IsRotatedOut(string refreshToken) => _refresh.TryGetValue(refreshToken, out var state) && state.RotatedOutAt is not null;

    /// <summary>Revokes every refresh token of a client, which is what "sign this user out everywhere" does
    /// at the provider. Returns how many it revoked.</summary>
    public int RevokeAllForClient(string clientId)
    {
        var revoked = 0;
        foreach (var state in _refresh.Values.Where(s => s.ClientId == clientId && !s.Revoked))
        {
            state.Revoked = true;
            revoked++;
        }

        return revoked;
    }

    /// <summary>Empties the request records and the counters. The clients and the keys stay.</summary>
    public void ResetLog()
    {
        _authorizations.Clear();
        _grants.Clear();
        _revocations.Clear();
        _logouts.Clear();
        _issuedAccessTokens.Clear();
        _issuedRefreshTokens.Clear();
        Volatile.Write(ref _discoveryFetches, 0);
        Volatile.Write(ref _jwksFetches, 0);
    }

    /// <summary>Reads an issued token without validating it, for a test that asserts on its claims.</summary>
    public static JsonWebToken ReadJwt(string token) => new JsonWebTokenHandler().ReadJsonWebToken(token);

    /// <summary>
    /// A short name for a token, for test output: <c>at#12</c>, <c>rt#7</c> or <c>(none)</c>. Never the whole
    /// value, so output can be read beside a log that must hold no token (N-16).
    /// </summary>
    public static string Label(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "(none)";
        if (token.StartsWith("rt~", StringComparison.Ordinal))
            return token.Split('~') is { Length: >= 2 } parts ? $"rt#{parts[1]}" : "(unrecognised)";

        try
        {
            return $"at#{ReadJwt(token).Id}";
        }
        catch (Exception e) when (e is ArgumentException or SecurityTokenException)
        {
            return "(unrecognised)";
        }
    }

    /// <summary>Starts the provider on a loopback port and fills in <see cref="BaseUrl"/>.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });

        // Whatever ASPNETCORE_FORWARDEDHEADERS_ENABLED says on this machine: the provider is not behind a proxy.
        builder.Configuration["ForwardedHeaders_Enabled"] = "false";
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        MapEndpoints(app);

        await app.StartAsync(cancellationToken);
        _app = app;
        BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_app is { } app)
        {
            await app.StopAsync();
            await app.DisposeAsync();
            _app = null;
        }

        _keys.Dispose();
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/.well-known/openid-configuration", async () =>
        {
            Interlocked.Increment(ref _discoveryFetches);
            await DelayAsync(Faults.DiscoveryDelay);
            var document = new Dictionary<string, object>
            {
                ["issuer"] = BaseUrl,
                ["authorization_endpoint"] = AuthorizeEndpoint,
                ["token_endpoint"] = TokenEndpoint,
                ["jwks_uri"] = JwksUri,
                ["response_types_supported"] = new[] { "code" },
                ["response_modes_supported"] = new[] { "query" },
                ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
                ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["code_challenge_methods_supported"] = new[] { "S256" },
                ["scopes_supported"] = new[] { "openid", "profile" },
            };

            // Cognito's discovery document lists no end_session_endpoint. That difference is what ADR-0011
            // puts behind IIdpProfile, so the Cognito style advertises nothing and the endpoint still works.
            if (EndSessionStyle == EndSessionStyle.Standard) document["end_session_endpoint"] = EndSessionEndpoint;
            if (Faults.AdvertiseRevocation) document["revocation_endpoint"] = RevocationEndpoint;
            return Results.Json(document);
        });

        app.MapGet("/jwks", async () =>
        {
            Interlocked.Increment(ref _jwksFetches);
            await DelayAsync(Faults.JwksDelay);
            return Results.Json(new { keys = _keys.PublishedKeys(Faults.PublishRetiredKeys) });
        });

        // There is no login page. Whoever the test says is typing is ?login_hint=bob, and DefaultSubject otherwise.
        app.MapGet("/authorize", async (HttpContext context) =>
        {
            await DelayAsync(Faults.AuthorizeDelay);
            var query = context.Request.Query;
            string clientId = query["client_id"].ToString(), redirectUri = query["redirect_uri"].ToString();
            var scope = query["scope"].ToString();
            var resource = Empty(query["resource"]) ? null : query["resource"].ToString();
            var subject = Empty(query["login_hint"]) ? DefaultSubject : query["login_hint"].ToString();

            IResult Refuse(string reason)
            {
                _authorizations.Enqueue(new Authorization(Interlocked.Increment(ref _authSeq), clientId, redirectUri, scope, resource, subject, reason));
                return Results.BadRequest(reason);
            }

            if (!_clients.TryGetValue(clientId, out var client)) return Refuse("unknown_client");
            if (!client.RedirectUris.Contains(redirectUri)) return Refuse("unregistered_redirect_uri");
            if (query["response_type"] != "code") return Refuse("unsupported_response_type");
            if (query["code_challenge_method"] != "S256" || Empty(query["code_challenge"])) return Refuse("pkce_required");

            var code = "code-" + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24));
            _codes[code] = new AuthorizationCode(clientId, redirectUri, subject, Empty(query["nonce"]) ? null : query["nonce"].ToString(),
                query["code_challenge"].ToString(), scope, resource);
            _authorizations.Enqueue(new Authorization(Interlocked.Increment(ref _authSeq), clientId, redirectUri, scope, resource, subject, "ok"));

            var back = new Dictionary<string, string?> { ["code"] = code };
            if (!Empty(query["state"])) back["state"] = query["state"].ToString();
            return Results.Redirect(QueryHelpers.AddQueryString(redirectUri, back));
        });

        app.MapPost("/token", async (HttpRequest request) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "invalid_request" });

            var form = await request.ReadFormAsync();
            var grantType = form["grant_type"].ToString();
            var (clientId, secret, auth) = ReadClient(request, form);
            bool sentResource = !Empty(form["resource"]), sentScope = !Empty(form["scope"]);
            var presented = grantType == "refresh_token" ? form["refresh_token"].ToString() : null;

            IResult Reject(string error, int status = 400)
            {
                LogGrant(new TokenGrant(0, grantType, clientId ?? "", auth, error, presented, null, null, sentResource, sentScope, false));
                return Results.Json(new { error }, statusCode: status);
            }

            await DelayAsync(Faults.TokenDelay);

            if (clientId is null || !_clients.TryGetValue(clientId, out var client) || client.Secret != secret)
                return Reject("invalid_client", StatusCodes.Status401Unauthorized);

            if (grantType == "authorization_code")
            {
                // A code is single use and bound to the client it was issued to (N-03, N-04).
                if (!_codes.TryRemove(form["code"].ToString(), out var code)) return Reject("invalid_grant");
                if (code.ClientId != clientId || code.RedirectUri != form["redirect_uri"].ToString()) return Reject("invalid_grant");
                var verifier = form["code_verifier"].ToString();
                if (verifier.Length == 0 || Pkce.Challenge(verifier) != code.Challenge) return Reject("invalid_grant");

                return Issue(grantType, clientId, auth, code.Subject, code.Scope, code.Resource, code.Nonce,
                    presented: null, familyId: null, sentResource, sentScope, replayedInsideGrace: false,
                    withRefreshToken: true, withIdToken: true);
            }

            if (grantType == "refresh_token")
            {
                if (Faults.RefreshOutageStatus is { } outage)
                {
                    LogGrant(new TokenGrant(0, grantType, clientId, auth, "http_" + outage, presented, null, null, sentResource, sentScope, false));
                    return Results.StatusCode(outage);
                }

                if (Faults.RefuseRefresh) return Reject("invalid_grant");

                // A refresh token belongs to the client it was issued to, and a rotated-out one works only
                // inside the grace window (evidence T-B7, T-B8).
                if (!_refresh.TryGetValue(presented!, out var state) || state.ClientId != clientId || state.Revoked) return Reject("invalid_grant");

                bool replayed;
                lock (state)
                {
                    var now = _clock.GetUtcNow();
                    if (state.RotatedOutAt is { } rotatedAt && now - rotatedAt > Faults.RefreshRotationGrace) return Reject("invalid_grant");
                    replayed = state.RotatedOutAt is not null;
                    if (Faults.RotateRefreshTokens) state.RotatedOutAt ??= now;
                }

                // The audience stays the one bound at login unless the request names another.
                var resource = sentResource ? form["resource"].ToString() : state.Resource;
                var answer = Issue(grantType, clientId, auth, state.Subject, state.Scope, resource, nonce: null,
                    presented, state.FamilyId, sentResource, sentScope, replayed,
                    withRefreshToken: Faults.RotateRefreshTokens, withIdToken: Faults.IdTokenOnRefresh);

                await DelayAsync(Faults.RefreshAnswerDelay);
                return answer;
            }

            return Reject("unsupported_grant_type");
        });

        // RFC 7009. An authenticated client always gets 200, whether or not the token is one of its own.
        app.MapPost("/revoke", async (HttpRequest request) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "invalid_request" });

            var form = await request.ReadFormAsync();
            var (clientId, secret, auth) = ReadClient(request, form);
            await DelayAsync(Faults.RevokeDelay);

            if (clientId is null || !_clients.TryGetValue(clientId, out var client) || client.Secret != secret)
                return Results.Json(new { error = "invalid_client" }, statusCode: StatusCodes.Status401Unauthorized);

            if (Faults.RevokeFailureStatus is { } failure) return Results.StatusCode(failure);

            var token = form["token"].ToString();
            var hint = Empty(form["token_type_hint"]) ? null : form["token_type_hint"].ToString();
            var known = _refresh.TryGetValue(token, out var state) && state.ClientId == clientId;
            var revoked = 0;

            if (known)
            {
                // Revoking one token leaves the rest of its family alone, which is what the G0 harness saw
                // (evidence T-B18). RevokeCascadesToFamily models a provider that revokes the family.
                IEnumerable<RefreshState> family = Faults.RevokeCascadesToFamily
                    ? _refresh.Values.Where(s => s.FamilyId == state!.FamilyId && !s.Revoked).ToArray()
                    : [state!];
                foreach (var member in family)
                {
                    member.Revoked = true;
                    revoked++;
                }
            }

            _revocations.Enqueue(new Revocation(Interlocked.Increment(ref _revokeSeq), clientId, auth, token, hint, known, revoked));
            return Results.Ok();
        });

        // Standard RP-initiated logout: id_token_hint, post_logout_redirect_uri, client_id and state.
        app.MapGet("/endsession", async (HttpContext context) =>
        {
            await DelayAsync(Faults.EndSessionDelay);
            var query = context.Request.Query;
            var back = query["post_logout_redirect_uri"].ToString();
            var clientId = Empty(query["client_id"]) ? ClientOfIdToken(query["id_token_hint"].ToString()) : query["client_id"].ToString();

            IResult Refuse(string reason)
            {
                _logouts.Enqueue(new Logout(Interlocked.Increment(ref _logoutSeq), EndSessionStyle.Standard, clientId, null, reason));
                return Results.BadRequest(reason);
            }

            if (clientId is not null && !_clients.ContainsKey(clientId)) return Refuse("unknown_client");

            // A hint-less logout is what a session whose ID token was blanked by a refresh sends
            // (evidence T-B13). The URI is then matched against every client's list.
            if (back.Length == 0)
            {
                _logouts.Enqueue(new Logout(Interlocked.Increment(ref _logoutSeq), EndSessionStyle.Standard, clientId, null, "ok"));
                return Results.Text("signed out");
            }

            if (!RegistersPostLogoutUri(clientId, back)) return Refuse("unregistered_post_logout_redirect_uri");

            var target = Empty(query["state"])
                ? back
                : QueryHelpers.AddQueryString(back, "state", query["state"].ToString());
            _logouts.Enqueue(new Logout(Interlocked.Increment(ref _logoutSeq), EndSessionStyle.Standard, clientId, back, "ok"));
            return Results.Redirect(target);
        });

        // Cognito's logout: client_id and logout_uri, no id_token_hint and no state (ADR-0011).
        app.MapGet("/logout", async (HttpContext context) =>
        {
            await DelayAsync(Faults.EndSessionDelay);
            var query = context.Request.Query;
            var clientId = query["client_id"].ToString();
            var back = query["logout_uri"].ToString();

            IResult Refuse(string reason)
            {
                _logouts.Enqueue(new Logout(Interlocked.Increment(ref _logoutSeq), EndSessionStyle.Cognito, clientId, null, reason));
                return Results.BadRequest(reason);
            }

            if (!_clients.TryGetValue(clientId, out var client)) return Refuse("unknown_client");
            if (back.Length == 0) return Refuse("missing_logout_uri");
            if (!client.PostLogoutUris.Contains(back)) return Refuse("unregistered_logout_uri");

            _logouts.Enqueue(new Logout(Interlocked.Increment(ref _logoutSeq), EndSessionStyle.Cognito, clientId, back, "ok"));
            return Results.Redirect(back);
        });
    }

    private IResult Issue(string grantType, string clientId, ClientAuth auth, string subject, string scope, string? resource, string? nonce,
        string? presented, string? familyId, bool sentResource, bool sentScope, bool replayedInsideGrace, bool withRefreshToken, bool withIdToken)
    {
        var serial = Interlocked.Increment(ref _seq);
        var now = _clock.GetUtcNow().UtcDateTime;
        var audience = Faults.IgnoreResource ? null : resource;
        var credentials = Faults.SignWithUnpublishedKey ? _keys.Unpublished : _keys.Current;

        var accessToken = CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,                    // resource mapped to aud: the resource binding the APIs check
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["username"] = subject,
                ["client_id"] = clientId,
                ["token_use"] = Faults.AccessTokenUse,
                ["scope"] = scope,
                ["jti"] = serial.ToString(),
            },
            SigningCredentials = credentials,
        }, now, Faults.AccessTokenLifetime);
        _issuedAccessTokens.Enqueue(accessToken);

        string? refreshToken = null;
        if (withRefreshToken)
        {
            refreshToken = $"rt~{serial}~{Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24))}{new string('p', Faults.RefreshTokenPadding)}";
            _refresh[refreshToken] = new RefreshState
            {
                FamilyId = familyId ?? "family-" + serial,
                ClientId = clientId,
                Subject = subject,
                Scope = scope,
                Resource = resource,
            };
            _issuedRefreshTokens.Enqueue(refreshToken);
        }
        else if (presented is not null)
        {
            refreshToken = presented;               // a provider that does not rotate hands the same one back
        }

        string? idToken = null;
        if (withIdToken)
        {
            var claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["name"] = subject + " (display)",
                ["email"] = subject + "@spa-poc.test",
                ["token_use"] = "id",
                ["auth_time"] = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds(),
                ["at_hash"] = AtHash(accessToken),
                ["jti"] = "id-" + serial,
            };
            if (!string.IsNullOrEmpty(nonce)) claims["nonce"] = nonce;

            idToken = CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = clientId,
                Claims = claims,
                SigningCredentials = credentials,
            }, now, Faults.IdTokenLifetime);
        }

        LogGrant(new TokenGrant(0, grantType, clientId, auth, "ok", presented, withRefreshToken ? refreshToken : null, accessToken,
            sentResource, sentScope, replayedInsideGrace));

        var body = new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)Faults.AccessTokenLifetime.TotalSeconds,
            ["scope"] = scope,
        };
        if (idToken is not null) body["id_token"] = idToken;
        if (refreshToken is not null) body["refresh_token"] = refreshToken;
        return Results.Json(body);
    }

    /// <summary>Signs one token. A negative lifetime issues an expired token, so <c>nbf</c> follows <c>exp</c>
    /// back rather than crossing it.</summary>
    private static string CreateToken(SecurityTokenDescriptor descriptor, DateTime now, TimeSpan lifetime)
    {
        var expires = now + lifetime;
        descriptor.IssuedAt = now;
        descriptor.NotBefore = expires < now ? expires.AddSeconds(-1) : now.AddSeconds(-5);
        descriptor.Expires = expires;
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>The <c>at_hash</c> of an access token: base64url of the left half of its SHA-256, as an
    /// RS256 ID token carries it.</summary>
    private static string AtHash(string accessToken)
        => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken))[..16]);

    private string? ClientOfIdToken(string idTokenHint)
    {
        if (idTokenHint.Length == 0) return null;
        try
        {
            return ReadJwt(idTokenHint).Audiences.FirstOrDefault();
        }
        catch (Exception e) when (e is ArgumentException or SecurityTokenException)
        {
            return null;
        }
    }

    private bool RegistersPostLogoutUri(string? clientId, string uri)
        => clientId is not null && _clients.TryGetValue(clientId, out var client)
            ? client.PostLogoutUris.Contains(uri)
            : _clients.Values.Any(c => c.PostLogoutUris.Contains(uri));

    private Task DelayAsync(TimeSpan delay) => delay > TimeSpan.Zero ? Task.Delay(delay, _clock) : Task.CompletedTask;

    private void LogGrant(TokenGrant grant) => _grants.Enqueue(grant with { Seq = Interlocked.Increment(ref _grantSeq) });

    private static bool Empty(StringValues value) => string.IsNullOrEmpty(value.ToString());

    /// <summary>
    /// <c>client_secret_basic</c> as RFC 6749 section 2.3.1 defines it, form-encoded and then base64, or
    /// <c>client_secret_post</c>. The two differ in practice: a handler's code exchange posts the secret in
    /// the body and a token library's refresh sends a Basic header (evidence T-B4, T-B5).
    /// </summary>
    private static (string? ClientId, string? Secret, ClientAuth Auth) ReadClient(HttpRequest request, IFormCollection form)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var raw = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var separator = raw.IndexOf(':', StringComparison.Ordinal);
                if (separator > 0)
                    return (Uri.UnescapeDataString(raw[..separator].Replace('+', ' ')),
                        Uri.UnescapeDataString(raw[(separator + 1)..].Replace('+', ' ')), ClientAuth.Basic);
            }
            catch (FormatException)
            {
                // A malformed header is no client at all.
            }

            return (null, null, ClientAuth.Basic);
        }

        if (!Empty(form["client_id"]))
            return (form["client_id"].ToString(), form["client_secret"].ToString(), ClientAuth.Post);

        return (null, null, ClientAuth.None);
    }
}
