using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Platform.Identity;

/// <summary>
/// The app's cookie scheme handler: the framework's, with one change (#727, shape (A) of #729). A session
/// store failure in its authenticate, the read of the session its cookie names on every path inside
/// <c>UseAuthentication</c>, is recorded on the request (<see cref="StoreFailure.Record"/>) and answered
/// with no result, so it touches no cookie and the request reaches <c>UsePlatformShellGate</c>, which ends
/// it under <c>auth.store</c>. The handler caches its result for the request
/// (AuthenticationHandler.HandleAuthenticateOnceAsync:
/// https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/Core/src/AuthenticationHandler.cs),
/// so a later authenticate on the request asks the store nothing. No result carries no failure, so
/// <see cref="AppSessions.KeyOf"/> notes no <c>auth.session</c> for it. It logs under the framework
/// handler's category (<see cref="FrameworkCategory"/>).
/// </summary>
internal sealed class PlatformCookieHandler(
    IOptionsMonitor<CookieAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : CookieAuthenticationHandler(options, new FrameworkCategory(logger, typeof(CookieAuthenticationHandler)), encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        try
        {
            return await base.HandleAuthenticateAsync();
        }
        catch (SessionStoreUnavailableException e)
        {
            StoreFailure.Record(Context, e);
            return AuthenticateResult.NoResult();
        }
    }
}

/// <summary>
/// The app's OpenID Connect scheme handler: the framework's, with one change (#727, shape (A) of #729).
/// The callback's sign-in, which creates the session through the cookie handler's ticket store, runs
/// after the remote handler's own try, so its failure never reaches the remote-failure event and its
/// static page (RemoteAuthenticationHandler.HandleRequestAsync:
/// https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/Core/src/RemoteAuthenticationHandler.cs).
/// It is caught here instead and ends the request under <c>auth.store</c> with the problem body. The
/// cookie handler appends the session cookie only once the store has answered, so none is sent.
///
/// And a second (#908): a challenge first reads the provider's discovery document through
/// <see cref="ProviderDiscovery"/>, which answers the request itself when the document cannot be read, so
/// the identity model's exception never leaves the challenge as a <c>500</c>. Only the read is guarded,
/// not the whole challenge, so any other failure of the handler's is still the gateway's generic one. It
/// logs under the framework handler's category (<see cref="FrameworkCategory"/>).
/// </summary>
internal sealed class PlatformOidcHandler(
    IOptionsMonitor<OpenIdConnectOptions> options, ILoggerFactory logger, HtmlEncoder htmlEncoder, UrlEncoder encoder)
    : OpenIdConnectHandler(options, new FrameworkCategory(logger, typeof(OpenIdConnectHandler)), htmlEncoder, encoder)
{
    public override async Task<bool> HandleRequestAsync()
    {
        try
        {
            return await base.HandleRequestAsync();
        }
        catch (SessionStoreUnavailableException e)
        {
            await StoreFailure.EndAsync(Context, e.Operation);
            return true;
        }
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (await ProviderDiscovery.ReadAsync(Context, Options))
        {
            await base.HandleChallengeAsync(properties);
        }
    }
}

/// <summary>
/// Hands a derived handler's base the logger of the framework handler it derives from. The framework
/// names a handler's logger after the runtime type (AuthenticationHandler's constructor, same source as
/// above), so without this the derived handlers would log under <c>Platform.Identity</c> and escape the
/// <c>Microsoft.AspNetCore.Authentication</c> floor that keeps the provider's error text off every line
/// (Platform.Telemetry's LogFloors, N-16). Owns nothing: disposing it leaves the factory it wraps alone.
/// </summary>
internal sealed class FrameworkCategory(ILoggerFactory inner, Type handler) : ILoggerFactory
{
    private readonly string category = handler.FullName!;

    public ILogger CreateLogger(string categoryName) => inner.CreateLogger(category);

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose()
    {
    }
}
