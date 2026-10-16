using Microsoft.AspNetCore.Http;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The token source double, which workstream 4 builds the proxy pipeline against (task 4.2: get the
/// token before forwarding, answer 401 when there is none).
///
/// <b>Frozen at G1b</b>, with <see cref="IAccessTokenSource"/> (ADR-0017).
/// </summary>
public sealed class FakeAccessTokenSourceTests
{
    [Fact]
    public async Task A_source_nobody_arranged_is_unauthenticated()
    {
        // The safe default. A test that forgot to arrange a token must get the 401 path, not a
        // token it never asked for.
        FakeAccessTokenSource source = new();

        Assert.IsType<AccessTokenResult.Unauthenticated>(await source.GetAsync(Context(), Ct));
    }

    [Fact]
    public async Task An_arranged_token_comes_back()
    {
        FakeAccessTokenSource source = new() { Result = new AccessTokenResult.Token("header.payload.signature") };

        AccessTokenResult result = await source.GetAsync(Context(), Ct);

        Assert.Equal("header.payload.signature", Assert.IsType<AccessTokenResult.Token>(result).Value);
    }

    [Fact]
    public async Task An_arranged_session_end_comes_back()
    {
        // Refresh failed with invalid_grant and the session was removed (task 3.8).
        FakeAccessTokenSource source = new() { Result = new AccessTokenResult.SessionEnded() };

        Assert.IsType<AccessTokenResult.SessionEnded>(await source.GetAsync(Context(), Ct));
    }

    [Fact]
    public async Task A_token_arranged_for_one_app_is_served_only_to_that_app()
    {
        // Two apps, two clients, two tokens. Test F-08 and N-17 need the mock API to see a token
        // that belongs to the caller, and a token that does not.
        FakeAccessTokenSource source = new();
        source.ForApp("app1", "app-one-token");
        source.ForApp("app2", "app-two-token");

        Assert.Equal("app-one-token", await TokenFor(source, "app1"));
        Assert.Equal("app-two-token", await TokenFor(source, "app2"));
    }

    [Fact]
    public async Task An_app_with_no_token_of_its_own_falls_back_to_the_arranged_result()
    {
        FakeAccessTokenSource source = new() { Result = new AccessTokenResult.Token("shared") };
        source.ForApp("app1", "app-one-token");

        Assert.Equal("app-one-token", await TokenFor(source, "app1"));
        Assert.Equal("shared", await TokenFor(source, "app2"));
    }

    [Fact]
    public async Task Every_call_is_recorded_with_the_app_it_was_asked_for()
    {
        // Task 4.2 acquires the token once, ahead of the forwarder. A test asserts that by
        // asserting this.
        FakeAccessTokenSource source = new() { Result = new AccessTokenResult.Token("t") };

        await source.GetAsync(ContextFor("app1"), Ct);
        await source.GetAsync(ContextFor("app2"), Ct);
        await source.GetAsync(ContextFor("app1"), Ct);

        Assert.Equal(["app1", "app2", "app1"], source.Calls);
    }

    [Fact]
    public async Task A_call_on_a_request_with_no_app_is_recorded_too()
    {
        // Nothing before host resolution should be asking for a token. If something does, the test
        // should be able to see it rather than have the double throw.
        FakeAccessTokenSource source = new();

        await source.GetAsync(Context(), Ct);

        Assert.Equal([null], source.Calls);
    }

    private static async Task<string> TokenFor(FakeAccessTokenSource source, string appId)
    {
        AccessTokenResult result = await source.GetAsync(ContextFor(appId), Ct);
        return Assert.IsType<AccessTokenResult.Token>(result).Value;
    }

    private static HttpContext Context() => new DefaultHttpContext();

    private static HttpContext ContextFor(string appId)
    {
        DefaultHttpContext context = new();
        context.Features.Set<IAppFeature>(SettableAppFeature.For(TestApps.Entry(appId)));
        return context;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
