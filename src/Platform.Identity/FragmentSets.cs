using Microsoft.AspNetCore.Authentication;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// A fragment's token set inside the session's ticket bytes (#994, the fragments spec #961 -> "The set
/// in the ticket"). <see cref="SessionRecord"/> gains no field: each fragment's set lives in the
/// ticket's properties, keyed by <c>fragmentId</c>, and records the client it was acquired with.
///
/// <list type="bullet">
/// <item>A token is <c>.Token.{name}:{fragmentId}</c>, the framework's own token prefix with the
/// fragment as a suffix, and stays out of <c>.TokenNames</c>, so the handler's
/// <c>StoreTokens</c> rewriting the shell's set (which clears every listed name first) leaves it alone
/// (https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/Core/src/AuthenticationTokenExtensions.cs).
/// The token library's scoped-token naming is not used.</item>
/// <item><c>.Fragment.Client:{fragmentId}</c> is the client the set or the mark was recorded to. One
/// that no longer matches the entry's reads as absent, so a fragment moved onto a new client reads
/// <c>loginRequired</c> and its old tokens are never sent for the new one.</item>
/// <item><c>.Fragment.Mark:{fragmentId}</c> is <c>unavailable</c> after a definitive refusal, and
/// never sits beside a set: writing a set removes it, and a mark is never written over a set.</item>
/// </list>
///
/// The one place these names are spelled: the per-set refresh and logout of #961's T5 read and write a
/// set through this type too.
/// </summary>
internal static class FragmentSets
{
    /// <summary>What <c>GET /bff/session</c> reports for a fragment.</summary>
    public const string SignedIn = "signedIn";

    /// <inheritdoc cref="SignedIn"/>
    public const string Unavailable = "unavailable";

    /// <inheritdoc cref="SignedIn"/>
    public const string LoginRequired = "loginRequired";

    /// <summary>The framework's prefix for a saved token's item (AuthenticationTokenExtensions.TokenKeyPrefix).</summary>
    private const string TokenPrefix = ".Token.";

    private const string ClientPrefix = ".Fragment.Client:";
    private const string MarkPrefix = ".Fragment.Mark:";

    /// <summary>
    /// The token a fragment's set is never kept with: the fragment's ID token is dropped at the merge,
    /// since the shell's ID token is the one sent on the provider's logout (#961 -> "The eager chain").
    /// </summary>
    private const string IdToken = "id_token";

    /// <summary>The item a fragment's token is kept under.</summary>
    public static string TokenItem(string name, string fragmentId) => TokenPrefix + name + ":" + fragmentId;

    /// <summary>
    /// How the ticket's properties stand for <paramref name="fragment"/>: <see cref="SignedIn"/> for a set
    /// with an access token recorded to the fragment's current client, <see cref="Unavailable"/> for a
    /// mark recorded to it, and <see cref="LoginRequired"/> for anything else, a set recorded to another
    /// client included.
    /// </summary>
    public static string StateOf(AuthenticationProperties properties, FragmentDefinition fragment)
    {
        ArgumentNullException.ThrowIfNull(properties);
        string id = fragment.FragmentId;
        if (!properties.Items.TryGetValue(ClientPrefix + id, out string? client)
            || !string.Equals(client, fragment.OAuth.ClientId, StringComparison.Ordinal))
        {
            return LoginRequired;
        }

        if (properties.Items.TryGetValue(TokenItem("access_token", id), out string? token) && !string.IsNullOrEmpty(token))
        {
            return SignedIn;
        }

        return properties.Items.TryGetValue(MarkPrefix + id, out string? mark) && mark == Unavailable ? Unavailable : LoginRequired;
    }

    /// <summary>
    /// Replaces the fragment's set with <paramref name="tokens"/>, whatever it held, recording its client
    /// and clearing any mark. The ID token is dropped.
    /// </summary>
    public static void WriteSet(AuthenticationProperties properties, FragmentDefinition fragment, IEnumerable<AuthenticationToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(properties);
        Clear(properties, fragment.FragmentId);
        foreach (AuthenticationToken token in tokens)
        {
            if (!string.Equals(token.Name, IdToken, StringComparison.Ordinal))
            {
                properties.Items[TokenItem(token.Name, fragment.FragmentId)] = token.Value;
            }
        }

        properties.Items[ClientPrefix + fragment.FragmentId] = fragment.OAuth.ClientId;
    }

    /// <summary>
    /// Marks the fragment <see cref="Unavailable"/>, recorded to its current client. False, and nothing
    /// changed, when the properties already hold the fragment as <see cref="SignedIn"/>: a mark never
    /// overwrites a set (#994, the W4 row).
    /// </summary>
    public static bool TryMark(AuthenticationProperties properties, FragmentDefinition fragment)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (StateOf(properties, fragment) == SignedIn)
        {
            return false;
        }

        Clear(properties, fragment.FragmentId);
        properties.Items[ClientPrefix + fragment.FragmentId] = fragment.OAuth.ClientId;
        properties.Items[MarkPrefix + fragment.FragmentId] = Unavailable;
        return true;
    }

    /// <summary>
    /// The fragment's token <paramref name="name"/>, or null when the set holds none, or when the set was
    /// recorded to a client other than the fragment's current one, which reads as absent (#995).
    /// </summary>
    public static string? Token(AuthenticationProperties properties, FragmentDefinition fragment, string name)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return properties.Items.TryGetValue(ClientPrefix + fragment.FragmentId, out string? client)
            && string.Equals(client, fragment.OAuth.ClientId, StringComparison.Ordinal)
            && properties.Items.TryGetValue(TokenItem(name, fragment.FragmentId), out string? value)
            && !string.IsNullOrEmpty(value)
                ? value
                : null;
    }

    /// <summary>
    /// Writes one token into the fragment's set, which keeps its other tokens and its recorded client: a
    /// refresh's write (#995). The ID token is never kept for a fragment.
    /// </summary>
    public static void SetToken(AuthenticationProperties properties, FragmentDefinition fragment, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (!string.Equals(name, IdToken, StringComparison.Ordinal))
        {
            properties.Items[TokenItem(name, fragment.FragmentId)] = value;
        }
    }

    /// <summary>Removes the fragment's set, its recorded client and any mark: a refusal's end of that set alone (#995).</summary>
    public static void Remove(AuthenticationProperties properties, string fragmentId)
    {
        ArgumentNullException.ThrowIfNull(properties);
        Clear(properties, fragmentId);
    }

    /// <summary>
    /// The fragments the ticket records a set or a mark for, with the client each was recorded to, in
    /// ordinal order of <c>fragmentId</c>: what logout finds in a record, a fragment the entry no longer
    /// lists included (#995).
    /// </summary>
    public static IReadOnlyList<(string FragmentId, string ClientId)> Recorded(AuthenticationProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return [.. properties.Items
            .Where(i => i.Key.StartsWith(ClientPrefix, StringComparison.Ordinal) && i.Value is not null)
            .Select(i => (i.Key[ClientPrefix.Length..], i.Value!))
            .OrderBy(i => i.Item1, StringComparer.Ordinal)];
    }

    /// <summary>The token <paramref name="name"/> recorded for <paramref name="fragmentId"/>, whatever client it was recorded to.</summary>
    public static string? RecordedToken(AuthenticationProperties properties, string fragmentId, string name)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return properties.Items.TryGetValue(TokenItem(name, fragmentId), out string? value) && !string.IsNullOrEmpty(value) ? value : null;
    }

    /// <summary>The names of the fragment's saved tokens, for a test or a reader that needs them all.</summary>
    public static IEnumerable<string> TokenNames(AuthenticationProperties properties, string fragmentId)
    {
        ArgumentNullException.ThrowIfNull(properties);
        string suffix = ":" + fragmentId;
        return properties.Items.Keys
            .Where(k => k.StartsWith(TokenPrefix, StringComparison.Ordinal) && k.EndsWith(suffix, StringComparison.Ordinal))
            .Select(k => k[TokenPrefix.Length..^suffix.Length]);
    }

    private static void Clear(AuthenticationProperties properties, string fragmentId)
    {
        foreach (string name in TokenNames(properties, fragmentId).ToList())
        {
            properties.Items.Remove(TokenItem(name, fragmentId));
        }

        properties.Items.Remove(ClientPrefix + fragmentId);
        properties.Items.Remove(MarkPrefix + fragmentId);
    }
}
