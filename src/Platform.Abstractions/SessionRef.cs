using System.Security.Cryptography;
using System.Text;

namespace Platform.Abstractions;

/// <summary>
/// The one implementation of <c>session_ref</c>, which contracts/telemetry.md defines as "the first
/// 12 hex characters of a SHA-256 of the session key".
///
/// It lives here, beside <see cref="IRequestTelemetryFeature"/>, for two reasons. The formula is in a
/// contract, so workstream 3 should not hand-roll the truncation and workstream 6's tests need to
/// compute the expected value the same way. And the session key passes through this method without
/// leaving workstream 3's code: what comes back is the reference, which is what may be logged.
///
/// The contract fixes the hash and the length. It fixes neither the encoding nor the case, so this
/// does: UTF-8 bytes, lowercase hex. Both have to be settled once, or two writers produce two
/// references for one session. No custom cryptography: this is the framework's SHA-256.
/// </summary>
public static class SessionRef
{
    /// <summary>How many hex characters a reference has. contracts/telemetry.md fixes it at 12.</summary>
    public const int Length = 12;

    /// <summary>
    /// The reference for a session key. Never log the argument; log what comes back.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The key is null or empty. An empty key would otherwise produce a perfectly valid-looking
    /// reference that every empty-keyed request shared.
    /// </exception>
    public static string Of(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionKey);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(sessionKey), hash);

        return Convert.ToHexStringLower(hash)[..Length];
    }
}
