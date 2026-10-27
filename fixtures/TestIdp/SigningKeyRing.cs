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
