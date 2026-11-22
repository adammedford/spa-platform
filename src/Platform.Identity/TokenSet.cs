using System.Globalization;
using Duende.AccessTokenManagement.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// One token set of a session: the tokens one client holds for it (#961 -> "Gates and leases per token
/// set"; #995). The shell's set is the ticket's own tokens, listed in <c>.TokenNames</c>; a fragment's is
/// kept under its suffixed names by <see cref="FragmentSets"/>. Each set has its own session gate, its
/// own refresh lease key and its own scheme, and a refresh reads and writes only its own set, so a
/// fragment's refresh never touches the shell's tokens and the shell's never touches a fragment's.
/// </summary>
internal abstract class TokenSet
{
    public const string AccessTokenName = "access_token";
    public const string RefreshTokenName = "refresh_token";
    public const string ExpiresAtName = "expires_at";
    public const string IdTokenName = "id_token";

    /// <summary>The shell's set.</summary>
    public static TokenSet Shell { get; } = new ShellSet();

    /// <summary>The set of <paramref name="fragment"/>, recorded to its current client.</summary>
    public static TokenSet Of(FragmentDefinition fragment) => new FragmentSet(fragment);

    /// <summary>The fragment the set belongs to, or null for the shell's.</summary>
    public abstract FragmentDefinition? Fragment { get; }

    /// <summary>
    /// The refresh lease's key: the session key for the shell's set, <c>{sessionKey}:{fragmentId}</c> for a
    /// fragment's (IRefreshLeases). It carries the session key, so it is never logged.
    /// </summary>
    public abstract string LeaseKey(string sessionKey);

    /// <summary>The OpenID Connect scheme the set's client is configured under.</summary>
    public abstract string ChallengeScheme(AppDefinition app);

    /// <summary>The set's token <paramref name="name"/>, or null when it holds none or reads as absent.</summary>
    public abstract string? Token(AuthenticationProperties properties, string name);

    /// <summary>Writes a refresh's answer into the set, leaving every other set in the ticket as it was.</summary>
    public abstract void Apply(AuthenticationProperties properties, UserToken token);

    /// <summary>When the set's access token expires; a token saved with no expiry is not known to expire, as the library treats it.</summary>
    public DateTimeOffset ExpiresAt(AuthenticationProperties properties) =>
        DateTimeOffset.TryParse(Token(properties, ExpiresAtName), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTimeOffset at)
            ? at
            : DateTimeOffset.MaxValue;

    private sealed class ShellSet : TokenSet
    {
        public override FragmentDefinition? Fragment => null;

        public override string LeaseKey(string sessionKey) => sessionKey;

        public override string ChallengeScheme(AppDefinition app) => SchemeNames.Oidc(app.AppId);

        public override string? Token(AuthenticationProperties properties, string name) =>
            properties.GetTokenValue(name) is { Length: > 0 } value ? value : null;

        /// <summary>
        /// The refresh token is replaced only when the provider rotated it, and the ID token only when the
        /// answer carried one (T-B13). <c>StoreTokens</c> clears only the names <c>.TokenNames</c> lists, so
        /// every fragment's suffixed items survive it (the S2 regression test).
        /// </summary>
        public override void Apply(AuthenticationProperties properties, UserToken token)
        {
            Dictionary<string, string> tokens = properties.GetTokens().ToDictionary(t => t.Name, t => t.Value, StringComparer.Ordinal);
            tokens[AccessTokenName] = token.AccessToken.ToString();
            tokens[ExpiresAtName] = token.Expiration.ToString("o", CultureInfo.InvariantCulture);
            if (token.RefreshToken is { } rotated)
            {
                tokens[RefreshTokenName] = rotated.ToString();
            }

            if (token.IdentityToken is { } identity)
            {
                tokens[IdTokenName] = identity.ToString();
            }

            properties.StoreTokens(tokens.Select(t => new AuthenticationToken { Name = t.Key, Value = t.Value }));
        }
    }

    private sealed class FragmentSet(FragmentDefinition fragment) : TokenSet
    {
        public override FragmentDefinition? Fragment => fragment;

        public override string LeaseKey(string sessionKey) => sessionKey + ":" + fragment.FragmentId;

        public override string ChallengeScheme(AppDefinition app) => SchemeNames.Oidc(app.AppId, fragment.FragmentId);

        public override string? Token(AuthenticationProperties properties, string name) => FragmentSets.Token(properties, fragment, name);

        /// <summary>A fragment's set keeps no ID token (#961 -> "The eager chain"), so the answer's is dropped.</summary>
        public override void Apply(AuthenticationProperties properties, UserToken token)
        {
            FragmentSets.SetToken(properties, fragment, AccessTokenName, token.AccessToken.ToString());
            FragmentSets.SetToken(properties, fragment, ExpiresAtName, token.Expiration.ToString("o", CultureInfo.InvariantCulture));
            if (token.RefreshToken is { } rotated)
            {
                FragmentSets.SetToken(properties, fragment, RefreshTokenName, rotated.ToString());
            }
        }
    }
}
