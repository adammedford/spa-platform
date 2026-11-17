using System.Collections.Immutable;

namespace Platform.Abstractions;

/// <summary>
/// The content security policy one host sends, as a pure function of its entry, so that the header
/// <c>UsePlatformSecurityHeaders</c> writes and the <c>script-src</c> <c>spactl</c>'s import-map write
/// check reads cannot disagree (the fragments spec, #961 → "The write check"). The baseline and the
/// header are contracts/http-contract.md -> Response headers. Today's merge in Platform.Security's
/// <c>SecurityHeaders</c> moves here, and for an entry with no fragments it sends what that merge sends,
/// except that a source the entry repeats from its directive's baseline or <c>form-action</c> origin
/// goes out once, which a browser enforces the same.
/// </summary>
public static class ContentSecurityPolicy
{
    /// <summary>The baseline, directive by directive, in the contract's order: the ten an entry may extend.</summary>
    public static readonly ImmutableArray<(string Directive, string Source)> Baseline =
    [
        ("default-src", "'self'"),
        ("script-src", "'self'"),
        ("style-src", "'self'"),
        ("img-src", "'self'"),
        ("font-src", "'self'"),
        ("connect-src", "'self'"),
        ("frame-ancestors", "'none'"),
        ("base-uri", "'self'"),
        ("object-src", "'none'"),
        ("form-action", "'self'"),
    ];

    /// <summary>The five directives a fragment's <c>csp</c> may extend.</summary>
    public static readonly ImmutableArray<string> FragmentDirectives =
        ["script-src", "style-src", "img-src", "font-src", "connect-src"];

    /// <summary>
    /// The host's one policy: for each directive of <see cref="Baseline"/>, in its order, a
    /// per-directive ordered union of the baseline source, <paramref name="formActionOrigin"/> in
    /// <c>form-action</c>, the shell's additions, then each fragment's additions in registry order,
    /// de-duplicated by ordinal comparison, so that a source repeated, the baseline's included, is sent
    /// once, at its first position. A fragment's additions are read only in
    /// <see cref="FragmentDirectives"/>; one in any other directive, which the validator refuses, is
    /// left out and not listed. A directive whose baseline is <c>'none'</c> loses it once any addition
    /// is kept. A source carrying <c>;</c>, <c>,</c>, whitespace or a control character is left out and
    /// listed in <see cref="MergedContentSecurityPolicy.Dropped"/>, so the policy is never looser than
    /// the entry asked. A directive outside the baseline is never sent.
    /// </summary>
    /// <param name="formActionOrigin">
    /// <c>Issuers:{name}:BrowserOrigin</c> of the shell's issuer, as written (a <see cref="Uri"/> would
    /// add a trailing slash). A fragment's issuer equals its shell's, so there is one.
    /// </param>
    public static MergedContentSecurityPolicy Merge(AppDefinition shell, string formActionOrigin)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(formActionOrigin);

        ImmutableArray<(string Directive, ImmutableArray<string> Sources)>.Builder directives =
            ImmutableArray.CreateBuilder<(string Directive, ImmutableArray<string> Sources)>(Baseline.Length);
        ImmutableArray<(string? FragmentId, string Directive)>.Builder dropped =
            ImmutableArray.CreateBuilder<(string? FragmentId, string Directive)>();

        // The loop walks the baseline, never an entry, so a directive outside the ten is never sent.
        foreach ((string directive, string baseline) in Baseline)
        {
            List<string> additions = [];
            Collect(shell.Csp, directive, fragmentId: null, additions, dropped);
            if (FragmentDirectives.Contains(directive, StringComparer.Ordinal))
            {
                foreach (FragmentDefinition fragment in shell.Fragments)
                {
                    Collect(fragment.Csp, directive, fragment.FragmentId, additions, dropped);
                }
            }

            // Beside another source 'none' has no effect, so a kept addition replaces it; with every
            // addition dropped the directive keeps 'none' rather than going out empty.
            // https://w3c.github.io/webappsec-csp/#match-url-to-source-list
            List<string> sources = baseline == None && additions.Count > 0 ? [] : [baseline];
            if (directive == "form-action")
            {
                AddOnce(sources, formActionOrigin);
            }

            foreach (string source in additions)
            {
                AddOnce(sources, source);
            }

            directives.Add((directive, [.. sources]));
        }

        return new MergedContentSecurityPolicy(directives.ToImmutable(), dropped.ToImmutable());
    }

    private const string None = "'none'";

    private static void Collect(
        ImmutableDictionary<string, ImmutableArray<string>> csp,
        string directive,
        string? fragmentId,
        List<string> kept,
        ImmutableArray<(string? FragmentId, string Directive)>.Builder dropped)
    {
        if (!csp.TryGetValue(directive, out ImmutableArray<string> additions))
        {
            return;
        }

        foreach (string source in additions)
        {
            if (CanSplice(source))
            {
                kept.Add(source);
            }
            else
            {
                dropped.Add((fragmentId, directive));
            }
        }
    }

    private static void AddOnce(List<string> sources, string source)
    {
        if (!sources.Contains(source, StringComparer.Ordinal))
        {
            sources.Add(source);
        }
    }

    /// <summary>
    /// False for a source that would end its directive (<c>;</c>) or its policy (<c>,</c>), which the
    /// CSP grammar reserves and a source expression never contains, and for whitespace or a control
    /// character. Spliced in as written they would add directives the entry may not set; dropping the
    /// source leaves the policy stricter than the entry asked, never looser.
    /// https://w3c.github.io/webappsec-csp/#parse-serialized-policy
    /// </summary>
    private static bool CanSplice(string source) =>
        !string.IsNullOrEmpty(source)
        && !source.Any(c => c is ';' or ',' || char.IsWhiteSpace(c) || char.IsControl(c));
}

/// <summary>
/// What <see cref="ContentSecurityPolicy.Merge"/> returns: each directive with its sources in the
/// order sent, and each source left out, by the fragment that added it (null for the shell) and its
/// directive, never by the source itself.
/// </summary>
public sealed record MergedContentSecurityPolicy(
    ImmutableArray<(string Directive, ImmutableArray<string> Sources)> Directives,
    ImmutableArray<(string? FragmentId, string Directive)> Dropped)
{
    /// <summary>The sources of one directive, empty for a directive the baseline does not name.</summary>
    public ImmutableArray<string> SourcesOf(string directive)
    {
        foreach ((string name, ImmutableArray<string> sources) in Directives)
        {
            if (string.Equals(name, directive, StringComparison.Ordinal))
            {
                return sources;
            }
        }

        return [];
    }

    /// <summary>The <c>Content-Security-Policy</c> value: <c>directive source source; …</c>.</summary>
    public string HeaderValue() =>
        string.Join("; ", Directives.Select(d => d.Directive + " " + string.Join(' ', d.Sources)));
}
