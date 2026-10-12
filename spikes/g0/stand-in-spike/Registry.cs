using System.Collections.Frozen;

namespace Spike;

/// <summary>One onboarded SPA. In the real platform this is the registry entry (secret is a reference, not a value).</summary>
public sealed record AppEntry(string AppId, string Host, string ClientId, string ClientSecret, string Scope);

/// <summary>Immutable, fully validated view of the registry. Requests pin one snapshot for their lifetime.</summary>
public sealed class RegistrySnapshot
{
    public required int Version { get; init; }
    public required FrozenDictionary<string, AppEntry> ByHost { get; init; }
    public required FrozenDictionary<string, AppEntry> ByAppId { get; init; }
}

public sealed class Registry
{
    private readonly object _writeLock = new();
    private RegistrySnapshot _current = Build(0, []);

    public RegistrySnapshot Current => Volatile.Read(ref _current);

    /// <summary>Raised after the swap. Ordering matters: swap first, evict caches second (see OptionsInvalidator).</summary>
    public event Action<RegistrySnapshot, RegistrySnapshot>? Swapped;

    /// <summary>Validate-then-swap. A snapshot that fails validation throws and the last-known-good one stays live.</summary>
    public void Publish(IEnumerable<AppEntry> apps)
    {
        lock (_writeLock)
        {
            var old = Current;
            var next = Build(old.Version + 1, apps.ToList());
            Volatile.Write(ref _current, next);
            Swapped?.Invoke(old, next);
        }
    }

    private static RegistrySnapshot Build(int version, IReadOnlyList<AppEntry> apps)
    {
        var dupHost = apps.GroupBy(a => a.Host, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dupHost is not null) throw new InvalidOperationException($"registry invalid: host '{dupHost.Key}' claimed twice");
        var dupId = apps.GroupBy(a => a.AppId, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (dupId is not null) throw new InvalidOperationException($"registry invalid: appId '{dupId.Key}' duplicated");
        var dupClient = apps.GroupBy(a => a.ClientId, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (dupClient is not null) throw new InvalidOperationException($"registry invalid: clientId '{dupClient.Key}' shared by two apps");
        foreach (var a in apps)
        {
            if (a.AppId.Contains(':') || string.IsNullOrWhiteSpace(a.AppId)) throw new InvalidOperationException("registry invalid: bad appId");
            if (string.IsNullOrWhiteSpace(a.ClientId) || string.IsNullOrWhiteSpace(a.ClientSecret)) throw new InvalidOperationException($"registry invalid: {a.AppId} has no client credentials");
        }
        return new RegistrySnapshot
        {
            Version = version,
            ByHost = apps.ToFrozenDictionary(a => a.Host, StringComparer.OrdinalIgnoreCase),
            ByAppId = apps.ToFrozenDictionary(a => a.AppId, StringComparer.Ordinal),
        };
    }
}

/// <summary>Host -> app resolution, pinned once per request so a reload mid-request cannot change the answer.</summary>
public static class RequestApp
{
    private static readonly object Key = new();

    public static AppEntry? Resolve(HttpContext ctx, Registry registry)
    {
        if (ctx.Items.TryGetValue(Key, out var cached)) return (AppEntry?)cached;
        registry.Current.ByHost.TryGetValue(ctx.Request.Host.Value ?? string.Empty, out var app); // exact authority match
        ctx.Items[Key] = app;
        return app;
    }
}

public static class SchemeNames
{
    private const string CookiePrefix = "cookie:";
    private const string OidcPrefix = "oidc:";
    public static string Cookie(string appId) => CookiePrefix + appId;
    public static string Oidc(string appId) => OidcPrefix + appId;
    public static bool TryParseCookie(string? name, out string appId) => TryParse(name, CookiePrefix, out appId);
    public static bool TryParseOidc(string? name, out string appId) => TryParse(name, OidcPrefix, out appId);

    private static bool TryParse(string? name, string prefix, out string appId)
    {
        if (name is not null && name.StartsWith(prefix, StringComparison.Ordinal)) { appId = name[prefix.Length..]; return true; }
        appId = string.Empty;
        return false;
    }
}
