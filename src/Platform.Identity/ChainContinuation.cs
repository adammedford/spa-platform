using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Platform.Identity;

/// <summary>
/// The continuation <c>c</c> of <c>GET /bff/chain</c> (#994; contracts/http-contract.md -> Fragments ->
/// "<c>/bff/chain</c>"; ADR-0040 decision 3): what the step needs to know and the caller must not choose.
/// It carries the session key it was minted for, the snapshot of the shell's <c>fragmentId</c>s in
/// registry order, the position of the next fragment, the validated local <c>returnUrl</c> from
/// <c>/bff/login</c>, and its expiry.
///
/// Protected by the data-protection key ring under a purpose naming the app and <c>chain</c>, so a
/// continuation minted on one app's host does not read on another's, and one that was changed does not
/// read at all. The payload is JSON inside the protected bytes, and the bytes travel base64url-encoded
/// in the query and in the fragment hop's protected state.
/// </summary>
internal sealed record ChainContinuation(
    string SessionKey,
    ImmutableArray<string> Fragments,
    int Position,
    string ReturnUrl,
    DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// <c>ChainLifetime</c>: five minutes from minting, renewed by each hop's new continuation
    /// (contracts/http-contract.md -> "<c>/bff/chain</c>").
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private const string Purpose = "Platform.Identity.Chain";

    /// <summary>The same chain with its next position and a fresh expiry from <paramref name="now"/>.</summary>
    public ChainContinuation Renewed(int position, DateTimeOffset now) => this with { Position = position, ExpiresAt = now + Lifetime };

    /// <summary>The protected string for <paramref name="appId"/>'s host.</summary>
    public string Protect(IDataProtectionProvider provider, string appId)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new Payload(SessionKey, [.. Fragments], Position, ReturnUrl, ExpiresAt.ToUnixTimeMilliseconds()));
        return WebEncoders.Base64UrlEncode(Protector(provider, appId).Protect(payload));
    }

    /// <summary>
    /// The continuation <paramref name="value"/> carries for <paramref name="appId"/>, or null for one that
    /// is missing, not base64url, not protected under this app's purpose, changed, or not this shape.
    /// Expiry and binding are the step's to check.
    /// </summary>
    public static ChainContinuation? Read(IDataProtectionProvider provider, string appId, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            byte[] payload = Protector(provider, appId).Unprotect(WebEncoders.Base64UrlDecode(value));
            if (JsonSerializer.Deserialize<Payload>(payload) is not { SessionKey: { } key, Fragments: { } fragments, ReturnUrl: { } returnUrl } read)
            {
                return null;
            }

            return new ChainContinuation(key, [.. fragments], read.Position, returnUrl, DateTimeOffset.FromUnixTimeMilliseconds(read.ExpiresAt));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Not the record's own ToString: the session key is on contracts/telemetry.md's never-logged list (N-16).
    public override string ToString() => nameof(ChainContinuation);

    private static IDataProtector Protector(IDataProtectionProvider provider, string appId) =>
        provider.CreateProtector(Purpose, appId);

    private sealed record Payload(string SessionKey, string[] Fragments, int Position, string ReturnUrl, long ExpiresAt);
}
