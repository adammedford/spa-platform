using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// Builds one app's OpenID Connect options when the framework first asks for its scheme by name
/// (plan row 3.2, ADR-0001). Nothing here does I/O: options setup is synchronous, the secret comes
/// preloaded from <see cref="IClientSecretSource"/>, and discovery is left to the shared metadata
/// manager, which fetches on the handler's first use rather than here (R-D10).
///
/// The app is the one host resolution pinned when it matches the scheme's name, so a request never
/// straddles a registry swap; otherwise it is the live snapshot's. A name that matches no app is left
/// unconfigured, and <see cref="OidcOptionsValidation"/> then refuses it (R-D9).
/// </summary>
public sealed class OidcOptionsSetup(
    IHttpContextAccessor accessor,
    IRegistry registry,
    IClientSecretSource secrets,
    IssuerDirectory issuers,
    IEnumerable<IIdpProfile> profiles,
    OptionsInvalidator invalidator) : IConfigureNamedOptions<OpenIdConnectOptions>
{
    /// <summary>The callback path, the same on every host: the host is what tells two apps apart (R-A2).</summary>
    public static readonly PathString CallbackPath = new("/signin-oidc");

    /// <summary>What every OpenID Connect scheme name starts with: <see cref="SchemeNames.Oidc(string)"/> of no app.</summary>
    internal static readonly string Prefix = SchemeNames.Oidc(string.Empty);

    public void Configure(OpenIdConnectOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        // Held, not called: building this setup builds the invalidator, so it has subscribed to the
        // registry before the first options it would evict exist (plan row 3.6).
        _ = invalidator;
        if (AppFor(name) is not { } app)
        {
            return;
        }

        IssuerDefinition issuer = issuers.Get(app.OAuth.Issuer);
        SharedOidc shared = issuers.Shared(app.OAuth.Issuer);

        options.ClientId = app.OAuth.ClientId;
        options.ClientSecret = secrets.Get(app.OAuth.ClientSecretRef);

        // Discovery and keys through the issuer's shared manager, never a per-app one (R-A7).
        options.Backchannel = shared.Backchannel;
        options.ConfigurationManager = shared.Metadata;

        // Code flow with PKCE (R-A1). Query, not the framework's form_post default: the cross-site POST
        // arrives without the SameSite=Lax correlation cookie and the login fails (R-D8). The handler
        // then leaves response_mode out of the request, as R-A1 expects.
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.UsePkce = true;

        // Tokens go to the ticket and so to the session store (ADR-0004, R-A8); claims keep the
        // provider's names rather than the framework's WS-Federation URIs.
        options.SaveTokens = true;
        options.MapInboundClaims = false;

        options.Scope.Clear();
        foreach (string scope in app.OAuth.Scopes)
        {
            options.Scope.Add(scope);
        }

        options.Resource = app.OAuth.Resource;
        options.CallbackPath = CallbackPath;

        // Named rather than left to the default sign-in scheme, so the callback can only ever sign in
        // to this app's own cookie scheme.
        options.SignInScheme = SchemeNames.Cookie(app.AppId);

        // Login-state cookies a sibling host cannot plant: __Host- requires Secure, Path=/ and no
        // Domain, and is the fix for login CSRF (R-D3, R-D4, ADR-0007).
        options.CorrelationCookie.Name = "__Host-spa-corr.";
        options.NonceCookie.Name = "__Host-spa-nonce.";
        foreach (CookieBuilder cookie in new[] { options.CorrelationCookie, options.NonceCookie })
        {
            cookie.Path = "/";
            cookie.Domain = null;
            cookie.SecurePolicy = CookieSecurePolicy.Always;
            cookie.HttpOnly = true;
            cookie.SameSite = SameSiteMode.Lax;
        }

        // The framework answers GET /signout-oidc by default, with no token and no origin check, which
        // signs the user out on a cross-site request (R-D1). Cleared, the path is not claimed (R-D2, N-27).
        options.RemoteSignOutPath = PathString.Empty;

        // The provider's own part last, so a profile may refine any of the above (ADR-0011). The
        // profile is chosen by the issuer's configured name, never by branching on it here. Every
        // setting above is plain OpenID Connect, so an issuer whose profile is not registered gets
        // those settings and no provider-specific refinement.
        profiles.FirstOrDefault(p => string.Equals(p.Name, issuer.Profile, StringComparison.Ordinal))
            ?.Configure(options, app, issuer);

        // The gateway's events after the profile, so they wrap what it set (plan row 3.5).
        AuthenticationEvents.Apply(options);
    }

    private AppDefinition? AppFor(string? name) =>
        name is not null && name.StartsWith(Prefix, StringComparison.Ordinal)
            ? SchemeApp.Find(accessor, registry, name[Prefix.Length..])
            : null;
}

/// <summary>
/// Runs the handler's own <see cref="OpenIdConnectOptions.Validate(string)"/> for every OpenID Connect
/// scheme. <c>AddOpenIdConnect</c> registers that validation only for the name it is given, so a scheme
/// named at runtime gets none: options with no client id are handed out without complaint (R-D9).
/// Registered here, an unknown or unconfigured name fails when it is built, not at the provider.
/// https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/Core/src/AuthenticationBuilder.cs
/// </summary>
public sealed class OidcOptionsValidation : IValidateOptions<OpenIdConnectOptions>
{
    public ValidateOptionsResult Validate(string? name, OpenIdConnectOptions options)
    {
        if (name is null || !name.StartsWith(OidcOptionsSetup.Prefix, StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Skip;
        }

        try
        {
            options.Validate(name);
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException e)
        {
            return ValidateOptionsResult.Fail(e.Message);
        }
        catch (ArgumentException e)
        {
            return ValidateOptionsResult.Fail(e.Message);
        }
    }
}
