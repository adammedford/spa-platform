using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TestIdp;

namespace Fixtures.Tests;

/// <summary>How a test authenticates its client on a back-channel request.</summary>
public enum AuthMode
{
    Basic,
    Post,
    None,
}

/// <summary>One answer from the token endpoint.</summary>
internal sealed record TokenAnswer(HttpStatusCode Status, JsonElement Body)
{
    public string? AccessToken => Value("access_token");

    public string? RefreshToken => Value("refresh_token");

    public string? IdToken => Value("id_token");

    public string? Scope => Value("scope");

    public string? Error => Value("error");

    public int? ExpiresIn => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("expires_in", out var v) ? v.GetInt32() : null;

    public bool Ok => Status == HttpStatusCode.OK;

    private string? Value(string name)
        => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>What one completed login left the caller holding.</summary>
internal sealed record Login(string Code, string Verifier, TokenAnswer Answer)
{
    public string AccessToken => Answer.AccessToken!;

    public string RefreshToken => Answer.RefreshToken!;

    public string IdToken => Answer.IdToken!;
}

/// <summary>
/// A started test identity provider and an HTTP client that does not follow redirects, with the two apps
/// of the plan's fixtures registered: app 1 with resource binding on the <c>orders</c> API, app 2 without.
/// </summary>
internal sealed class IdpHarness : IAsyncDisposable
{
    public const string Client1 = "client-1";
    public const string Secret1 = "secret-1";
    public const string Redirect1 = "https://app1.spa-poc.test:8443/signin-oidc";
    public const string PostLogout1 = "https://app1.spa-poc.test:8443/";
    public const string Client2 = "client-2";
    public const string Secret2 = "secret-2";
    public const string Redirect2 = "https://app2.spa-poc.test:8443/signin-oidc";
    public const string PostLogout2 = "https://app2.spa-poc.test:8443/";
    public const string Orders = "https://orders.spa-poc.test";
    public const string Scope = "openid profile https://orders.spa-poc.test/read";

    private IdpHarness(TestIdentityProvider idp, HttpClient http)
    {
        Idp = idp;
        Http = http;
    }

    public TestIdentityProvider Idp { get; }

    public HttpClient Http { get; }

    public static async Task<IdpHarness> StartAsync(TimeProvider? clock = null)
    {
        var idp = new TestIdentityProvider(clock);
        await idp.StartAsync();
        idp.AddClient(Client1, Secret1, Redirect1, PostLogout1);
        idp.AddClient(Client2, Secret2, Redirect2, PostLogout2);
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        return new IdpHarness(idp, http);
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Idp.DisposeAsync();
    }

    // ---- authorize ----

    public Task<HttpResponseMessage> AuthorizeAsync(IDictionary<string, string?> query)
    {
        var url = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(Idp.AuthorizeEndpoint, query);
        return Http.GetAsync(url);
    }

    public static Dictionary<string, string?> AuthorizeQuery(string challenge, string clientId = Client1, string redirectUri = Redirect1,
        string? resource = null, string? nonce = "nonce-1", string? state = "state-1", string? loginHint = null, string scope = Scope)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        if (nonce is not null) query["nonce"] = nonce;
        if (state is not null) query["state"] = state;
        if (resource is not null) query["resource"] = resource;
        if (loginHint is not null) query["login_hint"] = loginHint;
        return query;
    }

    /// <summary>Runs an authorize request and returns the code from the redirect.</summary>
    public async Task<string> CodeAsync(string challenge, string clientId = Client1, string redirectUri = Redirect1,
        string? resource = null, string? nonce = "nonce-1", string? loginHint = null, string scope = Scope)
    {
        using var response = await AuthorizeAsync(AuthorizeQuery(challenge, clientId, redirectUri, resource, nonce, loginHint: loginHint, scope: scope));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return QueryValue(response.Headers.Location!, "code");
    }

    public static string QueryValue(Uri location, string name)
        => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location.Query)[name].ToString();

    // ---- token ----

    public async Task<TokenAnswer> PostTokenAsync(Dictionary<string, string> form, string? clientId, string? secret, AuthMode auth)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Idp.TokenEndpoint);
        if (auth == AuthMode.Post && clientId is not null)
        {
            form["client_id"] = clientId;
            form["client_secret"] = secret ?? "";
        }

        request.Content = new FormUrlEncodedContent(form);
        if (auth == AuthMode.Basic && clientId is not null) request.Headers.Authorization = BasicHeader(clientId, secret ?? "");
        return await SendAsync(request);
    }

    public Task<TokenAnswer> CodeGrantAsync(string code, string verifier, string clientId = Client1, string secret = Secret1,
        string redirectUri = Redirect1, AuthMode auth = AuthMode.Post, string? resource = null)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        };
        if (verifier.Length > 0) form["code_verifier"] = verifier;
        if (resource is not null) form["resource"] = resource;
        return PostTokenAsync(form, clientId, secret, auth);
    }

    public Task<TokenAnswer> RefreshAsync(string refreshToken, string clientId = Client1, string secret = Secret1,
        AuthMode auth = AuthMode.Basic, string? resource = null)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        };
        if (resource is not null) form["resource"] = resource;
        return PostTokenAsync(form, clientId, secret, auth);
    }

    /// <summary>Authorize and exchange, the way a login runs.</summary>
    public async Task<Login> LoginAsync(string clientId = Client1, string secret = Secret1, string redirectUri = Redirect1,
        string? resource = null, string? nonce = "nonce-1", string? loginHint = null, string scope = Scope)
    {
        var verifier = Pkce.NewVerifier();
        var code = await CodeAsync(Pkce.Challenge(verifier), clientId, redirectUri, resource, nonce, loginHint, scope);
        var answer = await CodeGrantAsync(code, verifier, clientId, secret, redirectUri);
        Assert.True(answer.Ok, $"login failed: {answer.Error}");
        return new Login(code, verifier, answer);
    }

    // ---- revoke and logout ----

    public async Task<HttpResponseMessage> RevokeAsync(string token, string clientId = Client1, string secret = Secret1,
        string? hint = "refresh_token", AuthMode auth = AuthMode.Basic)
    {
        var form = new Dictionary<string, string> { ["token"] = token };
        if (hint is not null) form["token_type_hint"] = hint;
        if (auth == AuthMode.Post && clientId is not null)
        {
            form["client_id"] = clientId;
            form["client_secret"] = secret;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Idp.RevocationEndpoint) { Content = new FormUrlEncodedContent(form) };
        if (auth == AuthMode.Basic && clientId is not null) request.Headers.Authorization = BasicHeader(clientId, secret);
        return await Http.SendAsync(request);
    }

    public Task<HttpResponseMessage> EndSessionAsync(IDictionary<string, string?> query)
        => Http.GetAsync(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(Idp.EndSessionEndpoint, query));

    public Task<HttpResponseMessage> CognitoLogoutAsync(IDictionary<string, string?> query)
        => Http.GetAsync(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(Idp.CognitoLogoutEndpoint, query));

    // ---- documents and validation ----

    public async Task<JsonElement> DiscoveryAsync() => await GetJsonAsync(Idp.DiscoveryUri);

    public async Task<JsonElement> JwksAsync() => await GetJsonAsync(Idp.JwksUri);

    public async Task<JsonElement> GetJsonAsync(string url)
    {
        using var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>Validates a token against the published keys, the way an API behind the gateway does.</summary>
    public async Task<TokenValidationResult> ValidateAsync(string token, string? audience = null)
    {
        var jwks = await Http.GetStringAsync(Idp.JwksUri);
        return await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = Idp.BaseUrl,
            IssuerSigningKeys = JsonWebKeySet.Create(jwks).GetSigningKeys(),
            ValidateAudience = audience is not null,
            ValidAudience = audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        });
    }

    public static string? Claim(string token, string name)
        => TestIdentityProvider.ReadJwt(token).TryGetPayloadValue<string>(name, out var value) ? value : null;

    public static IReadOnlyList<string> Audiences(string token) => TestIdentityProvider.ReadJwt(token).Audiences.ToArray();

    private static AuthenticationHeaderValue BasicHeader(string clientId, string secret)
        => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(secret)}")));

    private async Task<TokenAnswer> SendAsync(HttpRequestMessage request)
    {
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var body = text.Length > 0 && text[0] == '{'
            ? JsonDocument.Parse(text).RootElement.Clone()
            : default;
        return new TokenAnswer(response.StatusCode, body);
    }
}
