using Microsoft.AspNetCore.Http;
using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// An <see cref="IAccessTokenSource"/> a test arranges. Set <see cref="Result"/> for every request,
/// or <see cref="ForApp"/> for one app at a time when a test needs two apps to carry two different
/// tokens, and <see cref="ForFragment"/> for one fragment of one app.
///
/// It starts out <see cref="AccessTokenResult.Unauthenticated"/>, so a test that arranges nothing
/// exercises the 401 path rather than silently acquiring a token. A request carrying
/// <see cref="IFragmentFeature"/> is answered from the fragment's arrangement alone, and starts out
/// <see cref="AccessTokenResult.FragmentLoginRequired"/>: an app's or the default result is never
/// returned for a fragment, as the real source never returns the shell's set on a fragment route.
/// </summary>
public sealed class FakeAccessTokenSource : IAccessTokenSource
{
    private readonly Dictionary<string, AccessTokenResult> byApp = new(StringComparer.Ordinal);
    private readonly Dictionary<(string AppId, string FragmentId), AccessTokenResult> byFragment = [];
    private readonly List<string?> calls = [];
    private readonly List<string?> fragmentCalls = [];

    /// <summary>The result for any app without one of its own.</summary>
    public AccessTokenResult Result { get; set; } = new AccessTokenResult.Unauthenticated();

    /// <summary>The result for any fragment without one of its own.</summary>
    public AccessTokenResult FragmentResult { get; set; } = new AccessTokenResult.FragmentLoginRequired();

    /// <summary>
    /// The app id behind each call, in order, or null where the request had no app feature. Task 4.2
    /// acquires the token once, ahead of the forwarder.
    /// </summary>
    public IReadOnlyList<string?> Calls => calls;

    /// <summary>
    /// The fragment id behind each call, in the order of <see cref="Calls"/>, or null where the request
    /// had no fragment feature.
    /// </summary>
    public IReadOnlyList<string?> FragmentCalls => fragmentCalls;

    /// <summary>Arranges a token for one app.</summary>
    public void ForApp(string appId, string token) => byApp[appId] = new AccessTokenResult.Token(token);

    /// <summary>Arranges any result for one app: a session that ended, or no session at all.</summary>
    public void ForApp(string appId, AccessTokenResult result) => byApp[appId] = result;

    /// <summary>Arranges a token for one fragment of one app.</summary>
    public void ForFragment(string appId, string fragmentId, string token) =>
        byFragment[(appId, fragmentId)] = new AccessTokenResult.Token(token);

    /// <summary>Arranges any result for one fragment of one app.</summary>
    public void ForFragment(string appId, string fragmentId, AccessTokenResult result) =>
        byFragment[(appId, fragmentId)] = result;

    public ValueTask<AccessTokenResult> GetAsync(HttpContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        string? appId = context.Features.Get<IAppFeature>()?.App.AppId;
        string? fragmentId = context.Features.Get<IFragmentFeature>()?.Fragment.FragmentId;
        calls.Add(appId);
        fragmentCalls.Add(fragmentId);

        // A fragment request reads the fragment's arrangement alone: the real source never returns the
        // shell's set on a fragment route.
        AccessTokenResult result = fragmentId is not null
            ? appId is not null && byFragment.TryGetValue((appId, fragmentId), out AccessTokenResult? forFragment)
                ? forFragment
                : FragmentResult
            : appId is not null && byApp.TryGetValue(appId, out AccessTokenResult? forApp)
                ? forApp
                : Result;

        return ValueTask.FromResult(result);
    }
}
