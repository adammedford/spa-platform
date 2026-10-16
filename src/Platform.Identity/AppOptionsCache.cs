using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The cache behind <see cref="IOptionsMonitor{TOptions}"/> for one kind of scheme options, in place of
/// the framework's <see cref="OptionsCache{TOptions}"/> (plan row 3.6). It caches by scheme name as
/// that one does, with two differences, each closing a way an evicted entry could come back.
///
/// <list type="bullet">
/// <item><b>A build that threw is not kept.</b> The framework's cache holds a <see cref="Lazy{T}"/>
/// per name, and a lazy value caches its exception, so one failed build fails every later read of the
/// name until something evicts it (R-D10: "3 Gets, 3 failures from 1 factory run"). Here the faulted
/// entry is removed before the exception leaves, so the next request builds again: a name the registry
/// did not have yet works as soon as a swap adds it (S-D1, R-C1).</item>
/// <item><b>Each entry remembers the fingerprint it was built from.</b> The options setups build from the
/// app host resolution pinned on the request (<see cref="SchemeApp"/>), so a request pinned before a
/// swap that builds after the eviction builds the old entry's options. It gets them, and never
/// straddles the swap; but they are cached only when that fingerprint is the live one, and a cached
/// entry is served only to a request pinned to the fingerprint it was built from.</item>
/// </list>
///
/// A name outside this kind's prefix, or one that names no app, is cached under no fingerprint, as the
/// framework would cache it.
/// https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Microsoft.Extensions.Options/src/OptionsCache.cs
/// </summary>
internal sealed class AppOptionsCache<TOptions>(IHttpContextAccessor accessor, IRegistry registry, string prefix)
    : IOptionsMonitorCache<TOptions>
    where TOptions : class
{
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public TOptions GetOrAdd(string? name, Func<TOptions> createOptions)
    {
        ArgumentNullException.ThrowIfNull(createOptions);
        name ??= Options.DefaultName;

        // The fingerprint the setup will build from for this request, and the live one.
        string? pinned = Fingerprint(name, pinnedFirst: true);
        string? live = Fingerprint(name, pinnedFirst: false);

        if (entries.TryGetValue(name, out Entry? cached) && cached.Fingerprint == pinned)
        {
            return Value(name, cached);
        }

        Entry fresh = new(pinned, new Lazy<TOptions>(createOptions, LazyThreadSafetyMode.ExecutionAndPublication));
        if (pinned != live)
        {
            // Built for this request only: its pinned entry has already been replaced.
            return fresh.Options.Value;
        }

        Entry chosen = cached is null
            ? entries.GetOrAdd(name, fresh)
            : entries.TryUpdate(name, fresh, cached) ? fresh : entries.GetOrAdd(name, fresh);
        return Value(name, chosen);
    }

    public bool TryAdd(string? name, TOptions options)
    {
        name ??= Options.DefaultName;
        return entries.TryAdd(name, new Entry(Fingerprint(name, pinnedFirst: false), new Lazy<TOptions>(options)));
    }

    public bool TryRemove(string? name) => entries.TryRemove(name ?? Options.DefaultName, out _);

    public void Clear() => entries.Clear();

    /// <summary>The entry's value. A build that threw leaves the cache before its exception leaves here (R-D10).</summary>
    private TOptions Value(string name, Entry entry)
    {
        try
        {
            return entry.Options.Value;
        }
        catch
        {
            // Only this entry: a concurrent request may already have put a good one in its place.
            entries.TryRemove(new KeyValuePair<string, Entry>(name, entry));
            throw;
        }
    }

    private string? Fingerprint(string name, bool pinnedFirst)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        string appId = name[prefix.Length..];
        AppDefinition? app = pinnedFirst
            ? SchemeApp.Find(accessor, registry, appId)
            : registry.Current.ByAppId.GetValueOrDefault(appId);
        return app?.Fingerprint;
    }

    private sealed record Entry(string? Fingerprint, Lazy<TOptions> Options);
}
