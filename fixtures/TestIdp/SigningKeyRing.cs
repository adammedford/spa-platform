using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace TestIdp;

/// <summary>
/// The provider's signing keys. One key is current, keys retired by a rotation stay listed in JWKS while
/// <see cref="TestIdpFaults.PublishRetiredKeys"/> is on, and one key is never listed at all, so a test can
/// mint a token whose signature no published key validates.
/// <para>
/// Key generation, the RSA parameters and their base64url encoding all come from
/// <c>System.Security.Cryptography</c> and Microsoft.IdentityModel. Nothing here is hand-written crypto.
/// </para>
/// </summary>
internal sealed class SigningKeyRing : IDisposable
{
    private readonly List<RsaSecurityKey> _keys = [];
    private readonly RsaSecurityKey _unpublished;
    private readonly Lock _gate = new();
    private int _generation;

    public SigningKeyRing()
    {
        _keys.Add(NewKey("test-key-1"));
        _unpublished = NewKey("test-key-unpublished");
        _generation = 1;
    }

    // RSA.Create defers generating the key to its first use, which would be the first /jwks request: up
    // to 190 ms on Linux, more on a loaded runner, inside a gateway test's sub-second provider deadline.
    // The export here generates it while the provider is built, outside any request.
    //
    // The runtime source at v10.0.0 says so. RSA.Create(int) creates the platform RSA and sets KeySize
    // (https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Security.Cryptography/src/System/Security/Cryptography/RSA.cs#L29-L43).
    // On Linux that is an RSAOpenSsl inside an RSAWrapper, which forwards KeySize and ExportParameters
    // (https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Security.Cryptography/src/System/Security/Cryptography/RSA.Create.OpenSsl.cs#L8-L11,
    // https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Security.Cryptography/src/System/Security/Cryptography/RSAWrapper.cs#L20-L24
    // and #L47-L48). RSAOpenSsl's KeySize setter stores the key as a Lazy over GenerateKey, and
    // ExportParameters(false) reaches GetKey, which reads that Lazy's Value
    // (https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Common/src/System/Security/Cryptography/RSAOpenSsl.cs#L42-L58,
    // #L319-L322, #L420-L446 and #L708-L725). On macOS that is an RSASecurityTransforms
    // (https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Security.Cryptography/src/System/Security/Cryptography/RSA.Create.SecurityTransforms.cs#L8-L11),
    // which leaves its key pair null until ExportParameters calls GetKeys, which generates it
    // (https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Common/src/System/Security/Cryptography/RSASecurityTransforms.cs#L49-L75
    // and #L605).
    private static RsaSecurityKey NewKey(string kid)
    {
        RSA rsa = RSA.Create(2048);
        rsa.ExportParameters(includePrivateParameters: false);
        return new(rsa) { KeyId = kid };
    }

    /// <summary>The key every token is signed with, unless the unpublished key is asked for.</summary>
    public SigningCredentials Current
    {
        get
        {
            lock (_gate) return Credentials(_keys[^1]);
        }
    }

    /// <summary>A key JWKS never lists.</summary>
    public SigningCredentials Unpublished => Credentials(_unpublished);

    private static SigningCredentials Credentials(RsaSecurityKey key) => new(key, SecurityAlgorithms.RsaSha256);

    /// <summary>Generates a new current key and returns its key id. The previous key is retired.</summary>
    public string Rotate()
    {
        lock (_gate)
        {
            var kid = "test-key-" + ++_generation;
            _keys.Add(NewKey(kid));
            return kid;
        }
    }

    /// <summary>The key id of the current key.</summary>
    public string CurrentKeyId
    {
        get
        {
            lock (_gate) return _keys[^1].KeyId;
        }
    }

    /// <summary>The public half of every key JWKS lists, newest last.</summary>
    public IReadOnlyList<object> PublishedKeys(bool includeRetired)
    {
        RsaSecurityKey[] listed;
        lock (_gate) listed = includeRetired ? [.. _keys] : [_keys[^1]];

        return [.. listed.Select(key =>
        {
            // The converter does the parameter export and the base64url encoding. Only kty, n and e are
            // published: a JWKS carries public material only.
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
            return (object)new { kty = jwk.Kty, use = "sig", alg = "RS256", kid = key.KeyId, n = jwk.N, e = jwk.E };
        })];
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var key in _keys) key.Rsa?.Dispose();
            _keys.Clear();
        }

        _unpublished.Rsa?.Dispose();
    }
}
