using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// An <see cref="IFragmentFeature"/> a test sets directly, standing in for host resolution's pinning
/// of a request under <c>/fragments/{fragmentId}/</c>. Prefer <see cref="For"/>, which takes the
/// fragment from the entry, the only pairing the real pipeline can produce; install it beside
/// <see cref="SettableAppFeature.For(AppDefinition)"/> for the same entry.
/// </summary>
public sealed class SettableFragmentFeature(FragmentDefinition fragment) : IFragmentFeature
{
    public FragmentDefinition Fragment { get; set; } = fragment;

    /// <summary>The feature for the fragment of <paramref name="app"/> named <paramref name="fragmentId"/>.</summary>
    /// <exception cref="ArgumentException">The entry lists no such fragment.</exception>
    public static SettableFragmentFeature For(AppDefinition app, string fragmentId)
    {
        ArgumentNullException.ThrowIfNull(app);

        foreach (FragmentDefinition fragment in app.Fragments)
        {
            if (string.Equals(fragment.FragmentId, fragmentId, StringComparison.Ordinal))
            {
                return new SettableFragmentFeature(fragment);
            }
        }

        throw new ArgumentException($"The entry '{app.AppId}' lists no fragment '{fragmentId}'.", nameof(fragmentId));
    }
}
