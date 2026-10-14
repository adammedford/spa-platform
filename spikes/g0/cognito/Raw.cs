using Duende.IdentityModel.Client;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Spike;

/// <summary>One answer from the token or revocation endpoint. The tokens are held for the next call, never printed.</summary>
public sealed record RawResult(bool Ok, int Status, string? Error, string? AccessToken, string? RefreshToken, string? IdToken)
{
    /// <summary>For a revocation: which client authentication style the provider took.</summary>
    public string? Note { get; init; }
    /// <summary>For a revocation the provider accepted: "form body" or "Basic header".</summary>
    public string? AcceptedStyle { get; init; }
    public string Outcome => Note is not null ? (Ok ? "accepted (" + Note + ")" : "refused (" + Note + ")") : Ok ? "granted" : $"refused {Status} {Error}";
}

/// <summary>
/// The item 2 questions need to choose which refresh token is sent, which the token library cannot do (Q5). These are
/// the protocol calls through Duende.IdentityModel, with nothing hand-written.
/// </summary>
public sealed class Raw(HttpClient http, string tokenEndpoint, string revocationEndpoint, Redactor redactor)
{
    public string RevocationEndpoint => revocationEndpoint;

    public async Task<RawResult> RefreshAsync(AppEntry app, string refreshToken, ClientCredentialStyle style)
    {
        var r = await http.RequestRefreshTokenAsync(new RefreshTokenRequest
        {
            Address = tokenEndpoint,
            ClientId = app.ClientId,
            ClientSecret = app.ClientSecret,
            ClientCredentialStyle = style,
            RefreshToken = refreshToken,
        });
        redactor.Add(r.AccessToken); redactor.Add(r.RefreshToken); redactor.Add(r.IdentityToken);
        return new RawResult(!r.IsError, (int)r.HttpStatusCode, r.Error, r.AccessToken, r.RefreshToken, r.IdentityToken);
    }

    public async Task<RawResult> RevokeAsync(AppEntry app, string refreshToken)
    {
        var (ok, status, outcome, style) = await Revocation.SendAsync(http, revocationEndpoint, app.ClientId, app.ClientSecret, refreshToken);
        return new RawResult(ok, status, ok ? null : outcome, null, null, null) { Note = outcome, AcceptedStyle = style };
    }
}

/// <summary>
/// RFC 7009 through Duende.IdentityModel. The form body first, as task 3.8 authenticates everywhere; if the provider
/// refuses the client that way, once more with a Basic header, and the outcome says which style it took. A revocation
/// that needed the Basic header is a Q17 answer: C-2.21 reports it, so the retry cannot hide a refused form body.
/// </summary>
public static class Revocation
{
    public static async Task<(bool Ok, int Status, string Outcome, string? AcceptedStyle)> SendAsync(HttpClient http, string endpoint, string clientId, string secret, string token)
    {
        TokenRevocationResponse Last = default!;
        var tried = new List<string>();
        foreach (var style in new[] { ClientCredentialStyle.PostBody, ClientCredentialStyle.AuthorizationHeader })
        {
            Last = await http.RevokeTokenAsync(new TokenRevocationRequest
            {
                Address = endpoint,
                ClientId = clientId,
                ClientSecret = secret,
                ClientCredentialStyle = style,
                Token = token,
                TokenTypeHint = "refresh_token",
            });
            var name = style == ClientCredentialStyle.PostBody ? "form body" : "Basic header";
            if (!Last.IsError) { tried.Add($"{name} {(int)Last.HttpStatusCode}"); return (true, (int)Last.HttpStatusCode, string.Join(", then ", tried), name); }
            tried.Add($"{name} refused {(int)Last.HttpStatusCode} {Last.Error}");
            if (!(Last.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized || Last.Error == "invalid_client")) break;   // refused for another reason: no second style
        }
        return (false, (int)Last.HttpStatusCode, string.Join(", then ", tried), null);
    }
}

/// <summary>The non-credential claims of an access token (Q4): aud, client_id, scope, token_use and exp. Read, not validated.</summary>
public static class Claims
{
    public sealed record Summary(string Aud, string ClientId, string Scope, string TokenUse, DateTimeOffset? Expires);

    public static Summary Of(string? token)
    {
        if (string.IsNullOrEmpty(token)) return new Summary("(no token)", "", "", "", null);
        try
        {
            var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token);
            string Claim(string name) => jwt.TryGetPayloadValue<object>(name, out var v) ? v?.ToString() ?? "" : "";
            var aud = jwt.Audiences.ToList();
            return new Summary(aud.Count == 0 ? "(none)" : string.Join(" ", aud), Claim("client_id"), Claim("scope"), Claim("token_use"),
                jwt.ValidTo == DateTime.MinValue ? null : new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero));
        }
        catch (ArgumentException)
        {
            return new Summary("(not a JWT)", "", "", "", null);
        }
    }

    public static string Describe(Summary s) => $"aud={s.Aud}, client_id={(s.ClientId.Length == 0 ? "(none)" : "present")}, scope={s.Scope}, token_use={s.TokenUse}";
}
