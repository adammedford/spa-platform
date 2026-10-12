using Microsoft.AspNetCore.Http;
using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// An <see cref="IAccessTokenSource"/> a test arranges. Set <see cref="Result"/> for every request,
/// or <see cref="ForApp"/> for one app at a time when a test needs two apps to carry two different
/// tokens.
///
/// It starts out <see cref="AccessTokenResult.Unauthenticated"/>, so a test that arranges nothing
/// exercises the 401 path rather than silently acquiring a token.
/// </summary>
public sealed class FakeAccessTokenSource : IAccessTokenSource
{
    private readonly Dictionary<string, AccessTokenResult> byApp = new(StringComparer.Ordinal);
    private readonly List<string?> calls = [];

    /// <summary>The result for any app without one of its own.</summary>
    public AccessTokenResult Result { get; set; } = new AccessTokenResult.Unauthenticated();

    /// <summary>
    /// The app id behind each call, in order, or null where the request had no app feature. Task 4.2
    /// acquires the token once, ahead of the forwarder.
    /// </summary>
    public IReadOnlyList<string?> Calls => calls;

    /// <summary>Arranges a token for one app.</summary>
    public void ForApp(string appId, string token) => byApp[appId] = new AccessTokenResult.Token(token);

    /// <summary>Arranges any result for one app: a session that ended, or no session at all.</summary>
    public void ForApp(string appId, AccessTokenResult result) => byApp[appId] = result;

    public ValueTask<AccessTokenResult> GetAsync(HttpContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string? appId = context.Features.Get<IAppFeature>()?.App.AppId;
        calls.Add(appId);

        AccessTokenResult result = appId is not null && byApp.TryGetValue(appId, out AccessTokenResult? forApp)
            ? forApp
            : Result;

        return ValueTask.FromResult(result);
    }
}
