using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Spike;

// Stand-in for OpenIdConnectHandler. NuGet is unreachable from this sandbox, and the real handler ships as a
// NuGet package, so this handler derives from the same framework base class (RemoteAuthenticationHandler<T>)
// and mirrors the real handler's shape: named options, per-scheme state protector, correlation cookie,
// callback-path dispatch, code redemption with client credentials, SignInAsync(SignInScheme).
// What it does NOT prove: anything specific to the real handler (ID-token validation, nonce, PKCE, discovery).

public sealed class StandInOidcOptions : RemoteAuthenticationOptions
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Scope { get; set; } = "";
    public ISecureDataFormat<AuthenticationProperties> StateDataFormat { get; set; } = default!;
}

/// <summary>Mirrors OpenIdConnectPostConfigureOptions: the state protector purpose includes the scheme name.</summary>
public sealed class StandInPostConfigure(IDataProtectionProvider dp) : IPostConfigureOptions<StandInOidcOptions>
{
    public void PostConfigure(string? name, StandInOidcOptions options)
    {
        ArgumentNullException.ThrowIfNull(name);
        options.DataProtectionProvider ??= dp;
        options.StateDataFormat ??= new PropertiesDataFormat(
            options.DataProtectionProvider.CreateProtector(typeof(StandInOidcHandler).FullName!, name, "v1"));
    }
}

public sealed class StandInOidcHandler(IOptionsMonitor<StandInOidcOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : RemoteAuthenticationHandler<StandInOidcOptions>(options, logger, encoder)
{
    public static long Initializations;                     // how many handler instances the framework created
    private const string ClientAtChallenge = ".client_id";
    private const string RedirectUriForCode = "StandIn.Code.RedirectUri";   // not ".redirect": that key IS AuthenticationProperties.RedirectUri

    protected override Task InitializeHandlerAsync()
    {
        Interlocked.Increment(ref Initializations);
        return Task.CompletedTask;
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (string.IsNullOrEmpty(properties.RedirectUri))
            properties.RedirectUri = OriginalPathBase + OriginalPath + Request.QueryString;

        GenerateCorrelationId(properties);
        var redirectUri = BuildRedirectUri(Options.CallbackPath);
        properties.Items[RedirectUriForCode] = redirectUri;
        properties.Items[ClientAtChallenge] = Options.ClientId;     // guard against the client changing mid-login

        var url = QueryHelpers.AddQueryString(Options.Authority + "/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = Options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = Options.Scope,
            ["state"] = Options.StateDataFormat.Protect(properties),
        });
        Response.Redirect(url);
        return Task.CompletedTask;
    }

    protected override async Task<HandleRequestResult> HandleRemoteAuthenticateAsync()
    {
        var state = Request.Query["state"].ToString();
        var code = Request.Query["code"].ToString();
        if (string.IsNullOrEmpty(state)) return HandleRequestResult.Fail("state missing");

        var properties = Options.StateDataFormat.Unprotect(state);
        if (properties is null) return HandleRequestResult.Fail("state invalid for this scheme");
        if (!ValidateCorrelationId(properties)) return HandleRequestResult.Fail("Correlation failed.", properties);

        if (!properties.Items.TryGetValue(ClientAtChallenge, out var startedAs) || startedAs != Options.ClientId)
            return HandleRequestResult.Fail("client changed during login; restart", properties);

        using var response = await Options.Backchannel.PostAsync(Options.Authority + "/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = properties.Items[RedirectUriForCode]!,
            ["client_id"] = Options.ClientId,
            ["client_secret"] = Options.ClientSecret,
        }), Context.RequestAborted);
        if (!response.IsSuccessStatusCode)
            return HandleRequestResult.Fail($"token endpoint rejected the code: {(int)response.StatusCode}", properties);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Context.RequestAborted));
        var root = json.RootElement;
        var identity = new ClaimsIdentity(Scheme.Name, "sub", "role");
        identity.AddClaim(new Claim("sub", root.GetProperty("sub").GetString()!));
        identity.AddClaim(new Claim("client_id", Options.ClientId));

        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = root.GetProperty("access_token").GetString()! },
            new AuthenticationToken { Name = "refresh_token", Value = root.GetProperty("refresh_token").GetString()! },
            new AuthenticationToken { Name = "expires_at", Value = SessionTokens.ExpiresAt(TimeProvider, root) },
        ]);
        return HandleRequestResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), properties, Scheme.Name));
    }
}

/// <summary>
/// Stand-in for the token-management library: returns the session's access token, refreshing it first when it
/// is about to expire. One refresh per session at a time; waiters re-read the session instead of refreshing again.
/// </summary>
public sealed class SessionTokens(SessionStore store, IOptionsMonitor<StandInOidcOptions> oidc, Registry registry, TimeProvider clock)
{
    private static readonly TimeSpan Skew = TimeSpan.FromMilliseconds(200);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public static string ExpiresAt(TimeProvider clock, JsonElement tokenResponse)
        => clock.GetUtcNow().AddMilliseconds(tokenResponse.GetProperty("expires_in_ms").GetInt32()).ToString("o", CultureInfo.InvariantCulture);

    private bool Fresh(AuthenticationProperties p)
        => DateTimeOffset.TryParse(p.GetTokenValue("expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
           && at - clock.GetUtcNow() > Skew;

    public async Task<string?> GetAccessTokenAsync(HttpContext ctx)
    {
        var auth = await ctx.AuthenticateAsync();                       // default scheme = this host's cookie scheme
        if (!auth.Succeeded || auth.Properties is null) return null;
        if (Fresh(auth.Properties)) return auth.Properties.GetTokenValue("access_token");

        var app = RequestApp.Resolve(ctx, registry)!;
        var key = (string)ctx.Items[AppBoundTicketStore.SessionKeyItem]!;
        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ctx.RequestAborted);
        try
        {
            var record = store.TryGet(key);                             // double-check under the lock
            if (record is null || record.AppId != app.AppId) return null;
            var ticket = TicketSerializer.Default.Deserialize(record.Ticket)!;
            if (Fresh(ticket.Properties)) return ticket.Properties.GetTokenValue("access_token");

            var o = oidc.Get(SchemeNames.Oidc(app.AppId));
            using var response = await o.Backchannel.PostAsync(o.Authority + "/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = ticket.Properties.GetTokenValue("refresh_token")!,
                ["client_id"] = o.ClientId,
                ["client_secret"] = o.ClientSecret,
            }), ctx.RequestAborted);
            if (!response.IsSuccessStatusCode)
            {
                await ctx.SignOutAsync();                               // refresh token dead: end the session
                return null;
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ctx.RequestAborted));
            ticket.Properties.UpdateTokenValue("access_token", json.RootElement.GetProperty("access_token").GetString()!);
            ticket.Properties.UpdateTokenValue("refresh_token", json.RootElement.GetProperty("refresh_token").GetString()!);
            ticket.Properties.UpdateTokenValue("expires_at", ExpiresAt(clock, json.RootElement));
            await ctx.SignInAsync(ticket.Principal, ticket.Properties);  // cookie handler -> ITicketStore.RenewAsync(same key)
            return ticket.Properties.GetTokenValue("access_token");
        }
        finally
        {
            gate.Release();
        }
    }
}
