using Duende.IdentityModel.Client;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using PlatformCredentialStyle = Platform.Abstractions.ClientCredentialStyle;

namespace Platform.Identity;

/// <summary>
/// ADR-0017: the issuer's client credential style governs the code exchange too. The handler's
/// exchange already implements PostBody; for Basic the supported protocol client redeems the code
/// and gives the complete response back to the handler, which still validates the ID token, nonce
/// and correlation and saves the tokens. No OAuth message or credential header is hand-built.
/// https://docs.duendesoftware.com/identitymodel/endpoints/token/
/// https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.authentication.openidconnect.authorizationcodereceivedcontext.handlecoderedemption
/// </summary>
internal static class CodeExchange
{
    public static void Apply(OpenIdConnectOptions options, PlatformCredentialStyle style, SharedOidc shared)
    {
        if (style != PlatformCredentialStyle.AuthorizationHeader)
        {
            return;
        }

        object redeemed = new();
        Func<TokenValidatedContext, Task> tokenValidated = options.Events.OnTokenValidated;
        options.Events.OnTokenValidated = async context =>
        {
            if (context.HttpContext.Items.ContainsKey(redeemed))
            {
                // HandleCodeRedemption skips this protocol validation in the framework. Its
                // TokenValidated event supplies the validated JWT and nonce read from the cookie;
                // run the same validator before any profile hook can accept the ticket (N-05).
                // https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/OpenIdConnect/src/OpenIdConnectHandler.cs
                context.Options.ProtocolValidator.ValidateTokenResponse(new OpenIdConnectProtocolValidationContext
                {
                    ClientId = context.Options.ClientId,
                    ProtocolMessage = context.TokenEndpointResponse,
                    ValidatedIdToken = context.SecurityToken,
                    Nonce = context.Nonce,
                });
            }

            await tokenValidated(context);
        };

        Func<AuthorizationCodeReceivedContext, Task> previous = options.Events.OnAuthorizationCodeReceived;
        options.Events.OnAuthorizationCodeReceived = async context =>
        {
            await previous(context);
            if (context.Result is not null || context.HandledCodeRedemption)
            {
                return;
            }

            OpenIdConnectMessage request = context.TokenEndpointRequest
                ?? throw new InvalidOperationException("The OIDC handler supplies its code redemption request.");
            OpenIdConnectConfiguration configuration = await shared.Metadata.GetConfigurationAsync(context.HttpContext.RequestAborted);
            using AuthorizationCodeTokenRequest redemption = new()
            {
                Address = configuration.TokenEndpoint,
                ClientId = request.ClientId,
                ClientSecret = request.ClientSecret,
                ClientCredentialStyle = ProfileTokenEndpoints.LibraryStyle(style),
                Code = request.Code,
                RedirectUri = request.RedirectUri,
                CodeVerifier = request.Parameters.TryGetValue("code_verifier", out string? verifier) ? verifier : null,
            };
            // Preserve provider parameters the handler or profile supplied, without duplicating the
            // fields owned by AuthorizationCodeTokenRequest (PKCE remains the handler's verifier).
            foreach ((string name, string value) in request.Parameters)
            {
                if (name is not ("grant_type" or "client_id" or "client_secret" or "code" or "redirect_uri" or "code_verifier"))
                {
                    redemption.Parameters.Add(name, value);
                }
            }

            TokenResponse response = await context.Backchannel.RequestAuthorizationCodeTokenAsync(redemption, context.HttpContext.RequestAborted);
            if (response.IsError || response.Json is null)
            {
                // Provider text is untrusted and may contain a credential (N-16). The normal remote
                // failure event owns the browser's constant failure page and auth.callback rule.
                context.Fail("The code exchange failed.");
                return;
            }

            context.HttpContext.Items[redeemed] = true;
            context.HandleCodeRedemption(new OpenIdConnectMessage(response.Json.Value.GetRawText()));
        };
    }
}
