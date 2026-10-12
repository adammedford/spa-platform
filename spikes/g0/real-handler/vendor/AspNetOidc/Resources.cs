// Review-only stand-in for the class the ASP.NET Core build generates from Resources.resx (values copied verbatim
// from src/Security/Authentication/OpenIdConnect/src/Resources.resx at release/10.0 9aaf828).
namespace Microsoft.AspNetCore.Authentication.OpenIdConnect;

internal static class Resources
{
    internal static string MessageStateIsNullOrEmpty => "OpenIdConnectAuthenticationHandler: message.State is null or empty.";
    internal static string MessageStateIsInvalid => "Unable to unprotect the message.State.";
    internal static string MessageContainsError => "Message contains error: '{0}', error_description: '{1}', error_uri: '{2}'.";
    internal static string ValidatedSecurityTokenNotJwt => "The Validated Security Token must be of type JwtSecurityToken, but instead its type is: '{0}'.";
    internal static string UnableToValidateToken => "Unable to validate the 'id_token', no suitable ISecurityTokenValidator was found for: '{0}'.\"";
    internal static string IdTokenCodeMissing => "Cannot process the message. Both id_token and code are missing.";
    internal static string ValidatedSecurityTokenNotJsonWebToken => "The Validated Security Token must be of type JsonWebToken, but instead its type is '{0}'.";
    internal static string UnableToValidateTokenFromHandler => "Unable to validate the 'id_token', no suitable TokenHandler was found for: '{0}'.\"";
}
