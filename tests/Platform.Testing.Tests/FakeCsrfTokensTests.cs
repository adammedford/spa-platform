using System.Text;
using Microsoft.AspNetCore.Http;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The antiforgery double. Workstream 4 owns the real one (task 4.4) and N-06 is proved against
/// that; this stands in for every other workstream's tests, and for the SPAs in task 5.1 posting
/// with the header.
/// </summary>
public sealed class FakeCsrfTokensTests
{
    [Fact]
    public void Issuing_returns_a_token_and_sets_the_cookie()
    {
        FakeCsrfTokens tokens = new();
        HttpContext context = ContextFor("app1");

        string token = tokens.Issue(context);

        Assert.NotEmpty(token);
        Assert.Contains("__Host-spa-csrf", SetCookie(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_carrying_the_issued_token_is_valid()
    {
        FakeCsrfTokens tokens = new();
        string token = tokens.Issue(ContextFor("app1"));

        Assert.True(await tokens.IsValidAsync(RequestFor("app1", token)));
    }

    [Fact]
    public async Task A_request_with_no_token_is_not_valid()
    {
        FakeCsrfTokens tokens = new();
        tokens.Issue(ContextFor("app1"));

        Assert.False(await tokens.IsValidAsync(RequestFor("app1", token: null)));
    }

    [Fact]
    public async Task A_request_with_the_wrong_token_is_not_valid()
    {
        FakeCsrfTokens tokens = new();
        tokens.Issue(ContextFor("app1"));

        Assert.False(await tokens.IsValidAsync(RequestFor("app1", "not-the-token")));
    }

    [Fact]
    public async Task A_token_issued_for_one_app_is_not_valid_on_another()
    {
        // The app id is bound into the token (task 4.4). R-E6: app 2 must not be able to post to
        // app 1 with its own token.
        FakeCsrfTokens tokens = new();
        string appOneToken = tokens.Issue(ContextFor("app1"));

        Assert.False(await tokens.IsValidAsync(RequestFor("app2", appOneToken)));
    }

    [Fact]
    public async Task Validation_never_reads_the_request_body()
    {
        // R-E7: the framework's antiforgery reads a form body when no header token is present, which
        // would consume a proxied request body. Header tokens only.
        FakeCsrfTokens tokens = new();
        HttpContext context = RequestFor("app1", token: null);
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("__RequestVerificationToken=whatever"));

        await tokens.IsValidAsync(context);

        Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public void Clearing_deletes_the_cookie()
    {
        // At logout (task 4.4).
        FakeCsrfTokens tokens = new();
        HttpContext context = ContextFor("app1");

        tokens.Clear(context);

        string setCookie = SetCookie(context);
        Assert.Contains("__Host-spa-csrf=", setCookie, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_source_forced_valid_accepts_a_request_with_no_token()
    {
        // For a workstream testing something else that happens to sit behind the check.
        FakeCsrfTokens tokens = new() { Force = true };

        Assert.True(await tokens.IsValidAsync(RequestFor("app1", token: null)));
    }

    [Fact]
    public async Task A_source_forced_invalid_rejects_the_token_it_issued()
    {
        FakeCsrfTokens tokens = new() { Force = false };
        string token = tokens.Issue(ContextFor("app1"));

        Assert.False(await tokens.IsValidAsync(RequestFor("app1", token)));
    }

    private static string SetCookie(HttpContext context) =>
        string.Join("; ", context.Response.Headers.SetCookie.ToArray());

    private static HttpContext ContextFor(string appId)
    {
        DefaultHttpContext context = new();
        context.Features.Set<IAppFeature>(SettableAppFeature.For(TestApps.Entry(appId)));
        return context;
    }

    private static HttpContext RequestFor(string appId, string? token)
    {
        HttpContext context = ContextFor(appId);
        if (token is not null)
        {
            context.Request.Headers["X-CSRF-TOKEN"] = token;
        }

        return context;
    }
}
