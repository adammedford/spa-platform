using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.WebUtilities;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// A provider that speaks plain OpenID Connect (plan row 3.7, ADR-0011). Everything the options setup
/// of task 3.2 sets is already standard, so there is nothing to refine, and logout is RP-initiated from
/// discovery: the handler builds the request to <c>end_session_endpoint</c> with <c>id_token_hint</c>
/// and the signed-out callback as <c>post_logout_redirect_uri</c>, adds its protected <c>state</c>
/// after the event, and the callback then lands on <c>/</c> (R-C6).
/// https://openid.net/specs/openid-connect-rpinitiated-1_0.html
/// </summary>
public sealed class StandardIdpProfile : IIdpProfile
{
    public string Name => "standard";

    /// <summary>The form body: the handler's own style at the code exchange (ADR-0017).</summary>
    public ClientCredentialStyle ClientCredentialStyle => ClientCredentialStyle.PostBody;

    public void Configure(OpenIdConnectOptions options, AppDefinition app, IssuerDefinition issuer)
    {
    }

    /// <summary>Keeps the request the handler built, so the handler redirects with it.</summary>
    public Task RedirectToSignOutAsync(RedirectContext context, AppDefinition app, IssuerDefinition issuer) =>
        Task.CompletedTask;
}

/// <summary>
/// Amazon Cognito (plan row 3.7, ADR-0011). Its logout is not RP-initiated: it is <c>GET /logout</c> on
/// the user pool's domain, which is the issuer's browser origin rather than its authority, with
/// <c>client_id</c> and a <c>logout_uri</c> registered on the app client, and it returns no
/// <c>state</c>. Sent back to the signed-out callback, the handler would answer that stateless return
/// with an empty page (R-C7), so the browser lands on the app's root instead, where the shell gate
/// starts a fresh login.
/// https://docs.aws.amazon.com/cognito/latest/developerguide/logout-endpoint.html
/// </summary>
public sealed class CognitoIdpProfile : IIdpProfile
{
    public string Name => "cognito";

    /// <summary>
    /// A Basic header: Cognito's revocation endpoint refuses a form-body secret with 401 (C-2.19,
    /// C-2.21, ADR-0017).
    /// https://docs.aws.amazon.com/cognito/latest/developerguide/revocation-endpoint.html
    /// </summary>
    public ClientCredentialStyle ClientCredentialStyle => ClientCredentialStyle.AuthorizationHeader;

    public void Configure(OpenIdConnectOptions options, AppDefinition app, IssuerDefinition issuer)
    {
    }

    /// <summary>
    /// Redirects to the pool's logout endpoint and marks the response handled, so the handler neither
    /// adds its state nor needs an <c>end_session_endpoint</c> from discovery. The return is the root of
    /// the host the logout was asked on, the same host the handler builds the login's redirect URI from.
    /// </summary>
    public Task RedirectToSignOutAsync(RedirectContext context, AppDefinition app, IssuerDefinition issuer)
    {
        HttpRequest request = context.Request;
        string root = UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase, "/");
        string logout = QueryHelpers.AddQueryString(new Uri(issuer.BrowserOrigin, "/logout").ToString(), new Dictionary<string, string?>
        {
            ["client_id"] = app.OAuth.ClientId,
            ["logout_uri"] = root,
        });

        context.Response.Redirect(logout);
        context.HandleResponse();
        return Task.CompletedTask;
    }
}
