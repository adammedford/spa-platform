using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace TestIdp;

/// <summary>
/// The PKCE pair a caller needs to drive the authorize and token endpoints: a verifier it keeps and the
/// S256 challenge it sends. The provider requires S256 and refuses a request without it.
/// </summary>
public static class Pkce
{
    /// <summary>A fresh verifier, 43 characters of base64url as RFC 7636 asks for.</summary>
    public static string NewVerifier() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>The S256 challenge for a verifier: base64url of the SHA-256 of its ASCII bytes.</summary>
    public static string Challenge(string verifier) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}
